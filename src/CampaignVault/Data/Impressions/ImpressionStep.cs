using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Context;
using CampaignVault.Data.Templates;
using CampaignVault.Data.Witness;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data.Impressions;

/// <summary>
/// The dispatcher's impression step (IMPRESSIONS_PLAN.md I5, I6): for every change in the batch that carries an appeal, each
/// observer is perceived (with sight), read, appraised, and the result is kept: an <see cref="Impression"/> on the observer, a
/// relationship change toward an author they know, a mood when it struck hard. One summary line per beat tells the narrator who
/// felt what and what they're inclined to do. It also settles <c>disbelieve</c> checks against earlier illusions (I6).
/// Runs inside the commit, after waking, so a sleeper the beat woke is an observer too.
/// </summary>
public sealed class ImpressionStep(PerceptibleBeats beats, SpellDefinitionProvider? spells = null)
{
    /// <summary>Impressions kept per character, newest first.</summary>
    public const int MaxImpressions = 10;

    /// <summary>How far back habituation looks: the same tone from the same actor within a day counts less each time.</summary>
    public const double HabituationHours = 24;

    private const int MaxObservers = 25;
    private const int MaxNamed = 8;

    public async Task ApplyAsync(ChangeContext ctx, IReadOnlyList<WorldChange> changes, CampaignTime time, CancellationToken ct = default)
    {
        var now = CampaignClock.Now(time);
        var system = ctx.Config?.ActiveSystem;
        foreach (var change in changes)
        {
            if (change is RulesetAction disbelief && disbelief.Parameters.ContainsKey("disbelieve"))
            {
                await SettleDisbeliefAsync(ctx, disbelief, system, ct);
            }

            if (Appeal.Of(change) is not { } appeal)
            {
                continue;
            }

            await ApplyBeatAsync(ctx, change, appeal, now, system, ct);
        }
    }

    private async Task ApplyBeatAsync(ChangeContext ctx, WorldChange change, Appeal appeal, double now, string? system, CancellationToken ct)
    {
        var (actorId, cause, addressed, eventLocation) = Describe(change);
        var actor = string.IsNullOrEmpty(actorId) ? null : await CharacterAsync(ctx, actorId, ct);
        var locationId = eventLocation ?? actor?.CurrentLocationId ?? ctx.Characters.Values.FirstOrDefault(c => c.IsPc)?.CurrentLocationId;
        if (string.IsNullOrEmpty(locationId))
        {
            return;
        }

        var action = change as RulesetAction;
        SpellDefinition? spell = null;
        if (action is { ActionType: RulesetActionType.Spell } && spells != null && system != null)
        {
            spells.TryGet(system, CastingComponentGate.NormalizeSlug(action.ActionName), out spell);
        }

        var beat = beats.Detect([change], system).FirstOrDefault();
        var signal = beat?.Signal ?? Perceptible.None;
        var magical = action?.ActionType == RulesetActionType.Spell;
        var illusion = magical && IllusionRules.IsIllusion(spell);
        var disbeliefDc = illusion && appeal.Deceptive
            ? (int.TryParse(action!.Parameters.GetValueOrDefault("dc")?.Trim(), out var given) ? given : IllusionRules.CasterDc(actor))
            : null;
        var facts = new BeatFacts(
            illusion,
            int.TryParse(action?.Parameters.GetValueOrDefault(NoticingContextContributor.RollTotalKey), out var total) ? total : null,
            action?.Parameters.GetValueOrDefault(RulesetActionHandler.ActorWonKey) is { } won ? won == "true" : null,
            new HashSet<string>(action?.ActionType == RulesetActionType.ContestedCheck ? action.TargetIds : [], StringComparer.OrdinalIgnoreCase),
            disbeliefDc,
            system);
        var quality = ImpressionResolver.QualityShift(action);

        var location = ctx.Locations.TryGetValue(locationId, out var known) ? known : await ctx.Session.LoadAsync<Location>(locationId, ct);
        var sight = new SightContext(SightResolver.LightAt(location, await ctx.GetCurrentTimeAsync()), beat?.Luminous == true, actor);
        var observers = await ObserversAsync(ctx, locationId, actor, signal, addressed, sight, ct);
        if (observers.Count == 0)
        {
            return;
        }

        var entries = new List<string>();
        foreach (var (observer, notice) in observers)
        {
            var read = ImpressionResolver.Read(observer, actor, appeal, facts);
            var repeats = observer.Impressions.Count(i => string.Equals(i.ActorId, actor?.Id, StringComparison.OrdinalIgnoreCase)
                                                          && string.Equals(i.IntendedTone, appeal.Tone.Name, StringComparison.OrdinalIgnoreCase)
                                                          && now - i.Hour <= HabituationHours);
            var appraisal = ImpressionResolver.Appraise(observer, actor, appeal, read,
                new AppraisalFacts(magical, repeats, notice.Outcome == Noticed.EffectNotSource, quality));

            var delta = Persist(ctx, observer, actor, appeal, read, appraisal, cause, disbeliefDc, now);
            entries.Add(Entry(observer, notice, read, appraisal, delta, actor));
        }

        var named = entries.Count > MaxNamed ? [.. entries.Take(MaxNamed), $"+{entries.Count - MaxNamed} others"] : entries;
        var by = actor != null ? $" by {actor.Name}" : "";
        ctx.RecordMessage($"IMPRESSIONS ({cause}{by}, {appeal.Tone.Name}{(appeal.Deceptive ? ", deceptive" : "")}): {string.Join(" | ", named)}. "
                          + "Recorded (opinions, moods). Narrate each: the reaction is a tendency, not a script.");
    }

    /// <summary>
    /// D3: a loud or visible beat reaches everyone at the location who perceives it (with sight, I1); a quiet one only those it was
    /// aimed at. PCs never: the player decides. Combatants do: the fight is where the scout's fear matters.
    /// </summary>
    private static async Task<List<(Character Observer, Notice Notice)>> ObserversAsync(ChangeContext ctx, string locationId, Character? actor,
        Perceptible signal, IReadOnlyList<string> addressed, SightContext sight, CancellationToken ct)
    {
        var result = new List<(Character, Notice)>();
        if (signal != Perceptible.None)
        {
            var indexed = await ctx.Session.Query<Character>()
                .Where(c => c.CampaignName == ctx.CampaignName && c.CurrentLocationId == locationId && !c.IsPc && c.Death == null)
                .Take(MaxObservers)
                .ToListAsync(ct);
            var present = indexed.Select(c => ctx.Characters.TryGetValue(c.Id, out var tracked) ? tracked : c)
                .Concat(ctx.Characters.Values.Where(c => string.Equals(c.CurrentLocationId, locationId, StringComparison.OrdinalIgnoreCase)))
                .Where(c => !c.IsPc && !c.IsDead && !string.Equals(c.Id, actor?.Id, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(c.CurrentLocationId, locationId, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(c => addressed.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
                .ThenByDescending(c => c.IsPartyCompanion || c.KeepAlive)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxObservers);
            foreach (var c in present)
            {
                var notice = WitnessSelector.Perceive(c, signal, actor, sight);
                if (notice.Perceived)
                {
                    result.Add((c, notice));
                }
            }

            return result;
        }

        foreach (var id in addressed.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(id, actor?.Id, StringComparison.OrdinalIgnoreCase) || await CharacterAsync(ctx, id, ct) is not { IsPc: false, IsDead: false } c)
            {
                continue;
            }

            // Spoken to or handed something: they take it in unless they can't take in anything at all.
            var notice = AwarenessResolver.Perceive(c, Perceptible.Both, actor);
            if (notice.Perceived)
            {
                result.Add((c, notice));
            }
        }

        return result;
    }

    /// <summary>Keeps the impression, and moves opinion (D4: only toward an author they know) and mood (a strong one).</summary>
    private static int Persist(ChangeContext ctx, Character observer, Character? actor, Appeal appeal, ReadResult read, Appraisal appraisal,
        string cause, int? disbeliefDc, double now)
    {
        if (!ctx.Characters.ContainsKey(observer.Id))
        {
            ctx.RegisterNewCharacter(observer);
        }

        observer.Impressions ??= [];
        observer.Impressions.Insert(0, new Impression
        {
            ActorId = actor?.Id,
            Tone = appraisal.Tone.Name,
            IntendedTone = appeal.Tone.Name,
            Tier = appraisal.Tier,
            Read = read.Read,
            KnowsAuthor = appraisal.KnowsAuthor,
            Cause = cause,
            DisbeliefDc = read.Read == ImpressionRead.Believed ? disbeliefDc : null,
            Hour = now,
        });
        if (observer.Impressions.Count > MaxImpressions)
        {
            observer.Impressions.RemoveRange(MaxImpressions, observer.Impressions.Count - MaxImpressions);
        }

        if (appraisal.Tier >= ImpressionTier.Strong && observer.Psychology != null)
        {
            observer.Psychology.CurrentMood = appraisal.Tone.Mood;
        }

        var delta = ImpressionResolver.RelationshipDelta(appraisal);
        if (delta != 0 && actor != null)
        {
            observer.Social ??= new SocialProfile();
            observer.Social.Relationships ??= new Dictionary<string, int>();
            var key = observer.Social.Relationships.Keys.FirstOrDefault(k => string.Equals(k, actor.Id, StringComparison.OrdinalIgnoreCase)) ?? actor.Id;
            observer.Social.Relationships[key] = Math.Clamp(observer.Social.Relationships.GetValueOrDefault(key) + delta, -100, 100);
        }

        return delta;
    }

    private static string Entry(Character observer, Notice notice, ReadResult read, Appraisal appraisal, int delta, Character? actor)
    {
        var feeling = appraisal.Tier switch
        {
            ImpressionTier.Faint => $"barely {appraisal.Tone.Mood}",
            ImpressionTier.Strong => $"very {appraisal.Tone.Mood}",
            ImpressionTier.Overwhelming => $"overwhelmingly {appraisal.Tone.Mood}",
            _ => appraisal.Tone.Mood,
        };
        var why = new List<string>();
        if (notice.Note != null)
        {
            why.Add(notice.Note);
        }

        if (read.Note != null)
        {
            why.Add(read.Note);
        }

        why.AddRange(appraisal.Reasons);
        if (delta != 0 && actor != null)
        {
            why.Add($"{delta:+0;-0} toward {actor.Name}");
        }

        var name = EncounterResolver.IsUnnamedStub(observer) ? $"unnamed stub [{observer.Id}]" : observer.Name;
        return $"{name}: {feeling}{(why.Count > 0 ? $" ({string.Join("; ", why)})" : "")} → {appraisal.Responds}";
    }

    /// <summary>
    /// I6: an observer who spent their action examining an illusion (a skill check with <c>disbelieve</c> = the caster's id) and
    /// met the DC sees through it: their latest believed impression of that caster's illusion turns. Short of it, they still
    /// believe. Says so either way.
    /// </summary>
    private static async Task SettleDisbeliefAsync(ChangeContext ctx, RulesetAction check, string? system, CancellationToken ct)
    {
        var casterId = check.Parameters.GetValueOrDefault("disbelieve")?.Trim();
        if (string.IsNullOrEmpty(casterId) || await CharacterAsync(ctx, check.CharacterId, ct) is not { } observer)
        {
            return;
        }

        var believed = observer.Impressions?.FirstOrDefault(i => i.Read == ImpressionRead.Believed && i.DisbeliefDc != null
                                                                && string.Equals(i.ActorId, casterId, StringComparison.OrdinalIgnoreCase));
        var skill = IllusionRules.DisbeliefSkill(system);
        if (believed is null)
        {
            ctx.RecordMessage($"DISBELIEVE: {observer.Name} has no illusion of {casterId}'s to see through; nothing recorded.");
            return;
        }

        if (!int.TryParse(check.Parameters.GetValueOrDefault(NoticingContextContributor.RollTotalKey), out var total))
        {
            return;
        }

        if (total < believed.DisbeliefDc)
        {
            ctx.RecordMessage($"DISBELIEVE: {observer.Name} examines the {believed.Cause} and still believes it ({skill} {total} vs DC {believed.DisbeliefDc}).");
            return;
        }

        var dc = believed.DisbeliefDc;
        var meant = Tones.TryGet(believed.IntendedTone, out var t) ? t : Tones.Get("suspicion");
        believed.Read = ImpressionRead.SawThrough;
        believed.Tone = Tones.SeenThrough(meant).Name;
        believed.DisbeliefDc = null;
        if (observer.Psychology != null && believed.Tier >= ImpressionTier.Strong)
        {
            observer.Psychology.CurrentMood = Tones.Get(believed.Tone).Mood;
        }

        // SRD 5.1: "If a creature discerns the illusion for what it is, the illusion becomes faint to the creature."
        ctx.RecordMessage($"DISBELIEVE: {observer.Name} sees through the {believed.Cause} ({skill} {total} vs DC {dc}): it's faint to them now, "
                          + $"and they take it as {believed.Tone}. Narrate it.");
    }

    private static (string? ActorId, string Cause, IReadOnlyList<string> Addressed, string? LocationId) Describe(WorldChange change) => change switch
    {
        RulesetAction a => (a.CharacterId, a.ActionName.Replace('_', ' ').ToLowerInvariant(), a.TargetIds, null),
        EventOccurred e => (e.InitiatorId ?? e.Involved?.FirstOrDefault(),
            PerceptibleBeats.Short(e.Summary),
            (e.Involved ?? []).Where(id => !string.Equals(id, e.InitiatorId ?? e.Involved?.FirstOrDefault(), StringComparison.OrdinalIgnoreCase)).ToList(),
            e.LocationId),
        _ => (null, "something", [], null),
    };

    private static async Task<Character?> CharacterAsync(ChangeContext ctx, string id, CancellationToken ct) =>
        ctx.Characters.TryGetValue(id, out var tracked) ? tracked : await ctx.Session.LoadAsync<Character>(id, ct);
}
