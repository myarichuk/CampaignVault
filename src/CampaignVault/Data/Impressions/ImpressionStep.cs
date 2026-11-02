using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Context;
using CampaignVault.Data.Templates;
using CampaignVault.Data.Witness;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;

namespace CampaignVault.Data.Impressions;

/// <summary>
/// The dispatcher's impression step (IMPRESSIONS_PLAN.md I5, I6): for every change in the batch that carries an appeal, each
/// observer is perceived (with sight), read, appraised, and the result is kept: an <see cref="Impression"/> on the observer, a
/// relationship change toward an author they know, a mood when it struck hard. One summary line per beat tells the narrator who
/// felt what and what they're inclined to do. It also settles <c>disbelieve</c> checks against earlier illusions (I6).
/// Runs inside the commit, after waking, so a sleeper the beat woke is an observer too.
/// </summary>
public sealed class ImpressionStep(PerceptibleBeats beats, SpellDefinitionProvider? spells = null, IRollService? rolls = null)
{
    /// <summary>Impressions kept per character, newest first.</summary>
    public const int MaxImpressions = 10;

    /// <summary>How far back habituation looks: the same tone from the same actor within a day counts less each time.</summary>
    public const double HabituationHours = 24;

    private const int MaxObservers = 25;
    private const int MaxNamed = 5;

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

            await ApplyBeatAsync(ctx, change, changes, appeal, now, system, ct);
        }
    }

    private async Task ApplyBeatAsync(ChangeContext ctx, WorldChange change, IReadOnlyList<WorldChange> batch, Appeal appeal, double now,
        string? system, CancellationToken ct)
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
        var illusion = magical && IllusionRules.IsFigment(spell);
        var disbeliefDc = illusion && appeal.Deceptive
            ? (int.TryParse(action!.Parameters.GetValueOrDefault("dc")?.Trim(), out var given) ? given : IllusionRules.CasterDc(actor))
            : null;
        var rolled = int.TryParse(action?.Parameters.GetValueOrDefault(NoticingContextContributor.RollTotalKey), out var total) ? total : (int?)null;
        var contest = action?.Parameters.GetValueOrDefault(RulesetActionHandler.ActorWonKey);
        bool? actorWon = contest is null ? null : contest == "true";
        // A skill check against a DC is the narrator's ruling on its targets (their Insight, by the narrator's reckoning).
        if (actor != null && action is { ActionType: RulesetActionType.SkillCheck } && rolled is { } t
            && int.TryParse(action.Parameters.GetValueOrDefault("dc")?.Trim(), out var targetDc))
        {
            actorWon = t >= targetDc;
        }

        var judged = action is { ActionType: RulesetActionType.ContestedCheck or RulesetActionType.SkillCheck } ? action.TargetIds : [];
        var facts = new BeatFacts(
            illusion,
            rolled,
            actorWon,
            new HashSet<string>(judged, StringComparer.OrdinalIgnoreCase),
            disbeliefDc,
            system,
            Tied: contest == "tie");
        var quality = ImpressionResolver.QualityShift(action);

        var location = ctx.Locations.TryGetValue(locationId, out var known) ? known : await ctx.Session.LoadAsync<Location>(locationId, ct);
        var clock = await ctx.GetCurrentTimeAsync();
        var sight = new SightContext(SightResolver.LightAt(location, clock), beat?.Luminous == true, actor);
        var present = await PresentAsync(ctx, locationId, ct);
        var observers = await ObserversAsync(ctx, present, actor, signal, addressed, sight, ct);
        if (observers.Count == 0)
        {
            return;
        }

        // F3: a deceptive illusion whose casting can be seen or heard gives anyone who caught it a reason to suspect its author.
        var casting = illusion && appeal.Deceptive && actor != null ? IllusionRules.CastingShows(spell, action!, system) : (Seen: false, Heard: false);
        var sleight = actor == null ? null : SleightTotal(batch, actor.Id);

        // F6: the narrator rules on what the fiction allows ("while he isn't looking", "gestures behind my back"); the engine
        // only applies it. unseenBy names who couldn't perceive the casting at all. concealCasting asks for a hidden gesture:
        // the engine rolls Sleight of Hand (pf2e Thievery) as part of the cast, no extra action, and passive Perception must meet it.
        var unseen = UnseenBy(action);
        var concealed = "";
        if (casting.Seen && sleight == null && rolls != null && actor != null && IsTrue(action?.Parameters.GetValueOrDefault("concealCasting")))
        {
            var skill = string.Equals(system, RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase) ? "Thievery" : "Sleight of Hand";
            var roll = await rolls.RollAsync(new RollRequest
            {
                Tag = "conceal-casting", Expression = "1d20", Bonus = ImpressionResolver.SkillBonus(actor, skill, dexterity: true)
            }, ct);
            sleight = roll.Result;
            concealed = $", gesture hidden: {skill} {roll.Result}";
        }
        var combat = ctx.ActiveCombat is { IsActive: true } encounter ? encounter : null;
        var addressedSet = new HashSet<string>(addressed, StringComparer.OrdinalIgnoreCase);

        var entries = new List<string>();
        var proposed = false;
        foreach (var (observer, notice) in observers)
        {
            var read = ImpressionResolver.Read(observer, actor, appeal, facts);
            if (read.Read == ImpressionRead.Believed && (casting.Seen || casting.Heard) && !unseen.All && !unseen.Ids.Contains(observer.Id)
                && NoticesCasting(observer, actor!, casting, sight, sleight, system) is { } caught)
            {
                var dc = disbeliefDc?.ToString() ?? "the spell DC";
                read = new ReadResult(ImpressionRead.Suspects,
                    $"{caught}: connects it to {actor!.Name}; examining it: {IllusionRules.DisbeliefSkill(system)} vs DC {dc}");
            }

            var repeats = actor is null ? 0 : observer.Impressions.Count(i => string.Equals(i.ActorId, actor.Id, StringComparison.OrdinalIgnoreCase)
                                                          && string.Equals(i.IntendedTone, appeal.Tone.Name, StringComparison.OrdinalIgnoreCase)
                                                          && now - i.Hour <= HabituationHours);
            var company = await CompanyOfAsync(ctx, observer, actor, present, combat, ct);
            var appraisal = ImpressionResolver.Appraise(observer, actor, appeal, read,
                new AppraisalFacts(magical, repeats, notice.Outcome == Noticed.EffectNotSource, quality, company));

            var grounds = actor != null && OpinionGrounds(observer, actor, addressedSet, combat);
            var delta = Persist(ctx, observer, actor, appeal, read, appraisal, cause, disbeliefDc, now, grounds, illusion);
            proposed |= delta != 0 && !grounds;
            entries.Add(Entry(observer, notice, read, appraisal, delta, grounds, actor));
        }

        var named = entries.Count > MaxNamed ? [.. entries.Take(MaxNamed), $"+{entries.Count - MaxNamed} others"] : entries;
        var by = actor != null ? $" by {actor.Name}" : "";
        var drawing = beat?.Draws == true ? ", drawing attention" : "";
        var unrecorded = proposed ? " Unrecorded opinion changes are yours to commit as relationship, or not." : "";
        change.ImpressionsReported = true;
        ctx.RecordMessage($"IMPRESSIONS ({cause}{by}, {appeal.Tone.Name}{(appeal.Deceptive ? ", deceptive" : "")}{drawing}{concealed}): {string.Join(" | ", named)}. "
                          + "Recorded. Each is a tendency, not a condition (don't commit frightened for it); a combatant acts on it on their own turn."
                          + unrecorded);
    }

    /// <summary>Everyone alive at the location, PCs included (they're company, never observers): the index plus this batch's arrivals.</summary>
    private static async Task<List<Character>> PresentAsync(ChangeContext ctx, string locationId, CancellationToken ct)
    {
        var indexed = await ctx.Session.Query<Character>()
            .Where(c => c.CampaignName == ctx.CampaignName && c.CurrentLocationId == locationId && c.Death == null)
            .Take(MaxObservers * 2)
            .ToListAsync(ct);
        return indexed.Select(c => ctx.Characters.TryGetValue(c.Id, out var tracked) ? tracked : c)
            .Concat(ctx.Characters.Values.Where(c => string.Equals(c.CurrentLocationId, locationId, StringComparison.OrdinalIgnoreCase)))
            .Where(c => !c.IsDead && string.Equals(c.CurrentLocationId, locationId, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// F1: the engine moves an opinion only where it has grounds to: the observer was addressed, already holds an opinion of the
    /// author, or rides with the party. Never for an opposing combatant. Anyone else's is proposed for the narrator to commit.
    /// </summary>
    private static bool OpinionGrounds(Character observer, Character actor, IReadOnlySet<string> addressed, CombatEncounter? combat)
    {
        if (combat != null && IsCombatant(combat, observer.Id) && IsCombatant(combat, actor.Id) && Side(observer) != Side(actor))
        {
            return false;
        }

        return addressed.Contains(observer.Id)
               || observer.IsPartyCompanion
               || observer.Social?.Relationships?.Keys.Any(k => string.Equals(k, actor.Id, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool IsCombatant(CombatEncounter combat, string id) =>
        combat.Combatants.Any(c => string.Equals(c.CharacterId, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The two sides as range validation draws them: the PCs and their companions, and everyone else.</summary>
    private static bool Side(Character c) => c.IsPc || c.IsPartyCompanion;

    /// <summary>
    /// F2: in a fight, allies are the observer's side of the encounter still standing; none is alone. Out of one, nobody else
    /// conscious here (the author aside) is alone; a recorded friend here (opinion 25+) is company; anything else is unknown.
    /// </summary>
    private static async Task<Company> CompanyOfAsync(ChangeContext ctx, Character observer, Character? actor, IReadOnlyList<Character> present,
        CombatEncounter? combat, CancellationToken ct)
    {
        if (combat != null && IsCombatant(combat, observer.Id))
        {
            foreach (var other in combat.Combatants.Where(c => !string.Equals(c.CharacterId, observer.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (await CharacterAsync(ctx, other.CharacterId, ct) is { IsDead: false } ally && ally.CurrentHp > 0 && Side(ally) == Side(observer)
                    && !AwarenessResolver.Conditions(ally).Overlaps(Insensible))
                {
                    return Company.Accompanied;
                }
            }

            return Company.Alone;
        }

        var others = present.Where(c => !string.Equals(c.Id, observer.Id, StringComparison.OrdinalIgnoreCase)
                                        && !string.Equals(c.Id, actor?.Id, StringComparison.OrdinalIgnoreCase)
                                        && c.CurrentHp > 0 && !AwarenessResolver.Conditions(c).Overlaps(Insensible))
            .ToList();
        if (others.Count == 0)
        {
            return Company.Alone;
        }

        return others.Any(o => ImpressionResolver.Opinion(observer, o.Id) >= FriendOpinion) ? Company.Accompanied : Company.Unknown;
    }

    private const int FriendOpinion = 25;

    private static readonly HashSet<string> Insensible = new(StringComparer.OrdinalIgnoreCase) { "unconscious", "petrified", "paralyzed" };

    /// <summary>F6: <c>parameters.unseenBy</c>, the narrator's ruling: character ids (comma-separated) or "all".</summary>
    private static (bool All, HashSet<string> Ids) UnseenBy(RulesetAction? action)
    {
        var raw = action?.Parameters.GetValueOrDefault("unseenBy");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (false, ids);
        }

        foreach (var id in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ids.Add(id);
        }

        return (ids.Contains("all"), ids);
    }

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Sleight of Hand check the caster made in this batch, to hide the gesture (F3).</summary>
    private static int? SleightTotal(IReadOnlyList<WorldChange> batch, string actorId) =>
        batch.OfType<RulesetAction>()
            .Where(a => string.Equals(a.CharacterId, actorId, StringComparison.OrdinalIgnoreCase) && ContextTurnReads.IsSkillCheck(a, "sleight"))
            .Select(a => int.TryParse(a.Parameters.GetValueOrDefault(NoticingContextContributor.RollTotalKey), out var t) ? t : (int?)null)
            .FirstOrDefault(t => t != null);

    /// <summary>
    /// F3: did this observer catch the casting? A hidden caster is noticed only by passive Perception against their Stealth. A
    /// gesture is seen by anyone with a sight line to the caster, unless a Sleight of Hand total beats their passive Perception; a
    /// spoken component is heard within Near by anyone not deafened. Returns what they caught, or null.
    /// </summary>
    private static string? NoticesCasting(Character observer, Character caster, (bool Seen, bool Heard) casting, SightContext sight, int? sleight,
        string? system)
    {
        var conditions = AwarenessResolver.Conditions(observer);
        if (conditions.Overlaps(Insensible) || AwarenessResolver.IsAsleep(observer))
        {
            return null;
        }

        if (caster.Concealment != null && !AwarenessResolver.NoticePresence(observer, caster, system: system).Perceived)
        {
            return null;
        }

        var invisible = AwarenessResolver.Conditions(caster).Contains("invisible");
        if (casting.Seen && !invisible && !conditions.Contains("blinded")
            && SightResolver.Sees(observer, sight with { Luminous = false, Origin = caster }).Sees)
        {
            var passive = AwarenessResolver.PassivePerception(observer, system: system);
            if (sleight is not { } hidden || passive >= hidden)
            {
                return sleight is { } s ? $"caught {caster.Name}'s gesture (Perception {passive} vs Sleight of Hand {s})" : $"saw {caster.Name} cast it";
            }
        }

        if (casting.Heard && !conditions.Contains("deafened") && SightResolver.Feet(SightResolver.BandBetween(observer, caster)) <= SightResolver.Feet(SpatialDistanceBand.Near))
        {
            return $"heard {caster.Name}'s incantation";
        }

        return null;
    }

    /// <summary>
    /// D3: a loud or visible beat reaches everyone at the location who perceives it (with sight, I1); a quiet one only those it was
    /// aimed at. PCs never: the player decides. Combatants do: the fight is where the scout's fear matters.
    /// </summary>
    private static async Task<List<(Character Observer, Notice Notice)>> ObserversAsync(ChangeContext ctx, IReadOnlyList<Character> everyone,
        Character? actor, Perceptible signal, IReadOnlyList<string> addressed, SightContext sight, CancellationToken ct)
    {
        var result = new List<(Character, Notice)>();
        if (signal != Perceptible.None)
        {
            var present = everyone
                .Where(c => !c.IsPc && !string.Equals(c.Id, actor?.Id, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Keeps the impression and the mood (a strong one), and the opinion change toward an author they know (D4) when the engine
    /// has grounds for it (F1); without grounds the change is only returned, for the line to propose.
    /// </summary>
    private static int Persist(ChangeContext ctx, Character observer, Character? actor, Appeal appeal, ReadResult read, Appraisal appraisal,
        string cause, int? disbeliefDc, double now, bool grounds, bool illusion)
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
            DisbeliefDc = read.Read is ImpressionRead.Believed or ImpressionRead.Suspects ? disbeliefDc : null,
            Illusion = illusion && read.Read is ImpressionRead.Believed or ImpressionRead.Suspects,
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
        if (delta != 0 && actor != null && grounds)
        {
            observer.Social ??= new SocialProfile();
            observer.Social.Relationships ??= new Dictionary<string, int>();
            var key = observer.Social.Relationships.Keys.FirstOrDefault(k => string.Equals(k, actor.Id, StringComparison.OrdinalIgnoreCase)) ?? actor.Id;
            observer.Social.Relationships[key] = Math.Clamp(observer.Social.Relationships.GetValueOrDefault(key) + delta, -100, 100);
        }

        return delta;
    }

    private static string Entry(Character observer, Notice notice, ReadResult read, Appraisal appraisal, int delta, bool recorded, Character? actor)
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
            why.Add(recorded ? $"{delta:+0;-0} toward {actor.Name}" : $"{delta:+0;-0} toward {actor.Name}, unrecorded [{observer.Id}]");
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

        var believed = observer.Impressions?.FirstOrDefault(i => (i.Illusion || i.DisbeliefDc != null)
                                                                && i.Read is ImpressionRead.Believed or ImpressionRead.Suspects
                                                                && string.Equals(i.ActorId, casterId, StringComparison.OrdinalIgnoreCase));
        var skill = IllusionRules.DisbeliefSkill(system);
        if (!ContextTurnReads.IsSkillCheck(check, skill))
        {
            ctx.RecordMessage($"DISBELIEVE: seeing through an illusion is an {skill} check here, not {check.Parameters.GetValueOrDefault("skill") ?? check.ActionName}; nothing settled.");
            return;
        }

        if (believed is null)
        {
            ctx.RecordMessage($"DISBELIEVE: {observer.Name} has no illusion of {casterId}'s to see through; nothing recorded.");
            return;
        }

        if (!int.TryParse(check.Parameters.GetValueOrDefault(NoticingContextContributor.RollTotalKey), out var total))
        {
            return;
        }

        // The DC the cast recorded, else the one the narrator set on this check: an NPC caster with no sheet DC still has one.
        var against = believed.DisbeliefDc ?? (int.TryParse(check.Parameters.GetValueOrDefault("dc")?.Trim(), out var given) ? given : (int?)null);
        if (against is null)
        {
            ctx.RecordMessage($"DISBELIEVE: the {believed.Cause}'s DC isn't known (the caster's sheet has none); pass parameters.dc on the check.");
            return;
        }

        believed.DisbeliefDc = against;
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
        believed.Illusion = false;
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
