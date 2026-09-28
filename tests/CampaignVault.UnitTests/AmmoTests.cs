using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class AmmoTests
{
    private readonly Character _actor = new() { Id = "chars/archer", Name = "Ilse", SystemStats = new SystemExtension() };

    private static Item Weapon(Dictionary<string, object>? props = null) => new()
    {
        Id = "items/bow", Name = "Shortbow", DefinitionName = "shortbow", HolderId = "chars/archer", CoreCategory = ItemCategories.Weapon,
        Properties = props ?? new() { ["ammoType"] = "arrow", ["damage"] = "1d6" }
    };

    private static Item Arrows(int charges, Dictionary<string, object>? props = null, string id = "items/arrows") => new()
    {
        Id = id, Name = "Arrows", HolderId = "chars/archer", CoreCategory = ItemCategories.Consumable,
        MaxCharges = 20, CurrentCharges = charges, ChargeUnit = "arrows",
        Properties = props ?? new() { ["ammoFor"] = "shortbow, longbow", ["uses"] = 20 }
    };

    private ChangeContext Ctx(params Item[] items)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            null, new() { [_actor.Id] = _actor }, items.ToDictionary(i => i.Id), [], [], [], NullLogger.Instance, [], dispatcher);
    }

    private static RulesetAction Attack(params (string k, string v)[] p)
    {
        var a = new RulesetAction
        {
            CharacterId = "chars/archer", TargetIds = ["chars/orc"], ActionType = RulesetActionType.Attack, ActionName = "Shortbow",
            Parameters = new() { ["weaponItemId"] = "items/bow" }
        };
        foreach (var (k, v) in p) a.Parameters[k] = v;
        return a;
    }

    [Fact]
    public async Task WeaponWithoutAmmoType_IsUntouched()
    {
        var ctx = Ctx(Weapon(new() { ["damage"] = "1d8" }));
        var (fail, plan) = await AmmoResolver.PrepareAsync(Attack(), ctx, false, TestContext.Current.CancellationToken);
        Assert.Null(fail);
        Assert.Null(plan);
    }

    [Fact]
    public async Task FindsAmmoByWeaponKey_AndSpendsOnePerShot()
    {
        var arrows = Arrows(20);
        var (fail, plan) = await AmmoResolver.PrepareAsync(Attack(), Ctx(Weapon(), arrows), false, TestContext.Current.CancellationToken);
        Assert.Null(fail);
        Assert.Equal(1, plan!.Spend);
        var report = AmmoResolver.Spend(plan);
        Assert.Equal(19, arrows.CurrentCharges);
        Assert.Contains("20 → 19", report);
    }

    [Fact]
    public async Task BurstMode_SetsCount_AndSpendsPerShot()
    {
        var weapon = Weapon(new() { ["ammoType"] = "arrow", ["ammoPerShot"] = "2", ["fireModes"] = "single:1, burst:3, auto:10" });
        var arrows = Arrows(20);
        var action = Attack(("mode", "burst"));
        var (fail, plan) = await AmmoResolver.PrepareAsync(action, Ctx(weapon, arrows), false, TestContext.Current.CancellationToken);
        Assert.Null(fail);
        Assert.Equal("3", action.Parameters["attackCount"]);
        Assert.Equal(6, plan!.Spend);
    }

    [Fact]
    public async Task ExplicitCount_BeatsMode_AndUnknownModeFails()
    {
        var weapon = Weapon(new() { ["ammoType"] = "arrow", ["fireModes"] = "burst:3" });
        var action = Attack(("mode", "burst"), ("attackCount", "2"));
        var (_, plan) = await AmmoResolver.PrepareAsync(action, Ctx(weapon, Arrows(20)), callerSetCount: true, TestContext.Current.CancellationToken);
        Assert.Equal("2", action.Parameters["attackCount"]);
        Assert.Equal(2, plan!.Spend);

        var (fail, _) = await AmmoResolver.PrepareAsync(Attack(("mode", "auto")), Ctx(weapon, Arrows(20)), false, TestContext.Current.CancellationToken);
        Assert.False(fail!.Value.Success);
        Assert.Contains("burst:3", fail.Value.Message);
    }

    [Fact]
    public async Task NotEnoughAmmo_ClampsTheBurst_AndSaysSo()
    {
        var weapon = Weapon(new() { ["ammoType"] = "arrow", ["fireModes"] = "auto:10" });
        var arrows = Arrows(4);
        var action = Attack(("mode", "auto"));
        var (fail, plan) = await AmmoResolver.PrepareAsync(action, Ctx(weapon, arrows), false, TestContext.Current.CancellationToken);
        Assert.Null(fail);
        Assert.Equal("4", action.Parameters["attackCount"]);
        var report = AmmoResolver.Spend(plan!);
        Assert.Equal(0, arrows.CurrentCharges);
        Assert.Contains("fired 4 of the requested", report);
    }

    [Fact]
    public async Task NoAmmoOrEmptyStack_FailsTheAttack()
    {
        var (none, _) = await AmmoResolver.PrepareAsync(Attack(), Ctx(Weapon()), false, TestContext.Current.CancellationToken);
        Assert.False(none!.Value.Success);
        Assert.Contains("NoAmmo", none.Value.Message);

        var (empty, _) = await AmmoResolver.PrepareAsync(Attack(), Ctx(Weapon(), Arrows(0)), false, TestContext.Current.CancellationToken);
        Assert.False(empty!.Value.Success);
    }

    [Fact]
    public async Task WrongAmmo_IsNotUsed_AndSkipsEmptyStacksForFullOnes()
    {
        var bolts = Arrows(20, new() { ["ammoFor"] = "crossbow_light" }, "items/bolts");
        var (fail, _) = await AmmoResolver.PrepareAsync(Attack(), Ctx(Weapon(), bolts), false, TestContext.Current.CancellationToken);
        Assert.False(fail!.Value.Success);

        var spent = Arrows(0, id: "items/a-empty");
        var full = Arrows(5, id: "items/b-full");
        var (ok, plan) = await AmmoResolver.PrepareAsync(Attack(), Ctx(Weapon(), spent, full), false, TestContext.Current.CancellationToken);
        Assert.Null(ok);
        Assert.Same(full, plan!.Ammo);
    }

    [Fact]
    public async Task LooseStackWithoutCharges_UsesQuantity_AndAmmoTypeMatchWorks()
    {
        var mags = new Item
        {
            Id = "items/mags", Name = "9mm rounds", HolderId = "chars/archer", CoreCategory = ItemCategories.Consumable,
            Quantity = 30, Properties = new() { ["ammoType"] = "9mm" }
        };
        var gun = Weapon(new() { ["ammoType"] = "9mm", ["fireModes"] = "burst:5" });
        var action = Attack(("mode", "burst"));
        var (fail, plan) = await AmmoResolver.PrepareAsync(action, Ctx(gun, mags), false, TestContext.Current.CancellationToken);
        Assert.Null(fail);
        AmmoResolver.Spend(plan!);
        Assert.Equal(25, mags.Quantity);
    }

    [Fact]
    public async Task TrickArrow_SeedsRiderParameters()
    {
        var fire = Arrows(1, new() { ["ammoFor"] = "shortbow", ["damage"] = "1d6", ["damageType"] = "fire" });
        var action = Attack();
        await AmmoResolver.PrepareAsync(action, Ctx(Weapon(), fire), false, TestContext.Current.CancellationToken);
        Assert.Equal("1d6", action.Parameters["riderDice"]);
        Assert.Equal("fire", action.Parameters["riderType"]);
    }

    [Fact]
    public async Task ExplicitAmmoItem_MustBeCarriedByTheActor()
    {
        var elsewhere = Arrows(10);
        elsewhere.HolderId = "chars/someone-else";
        var (fail, _) = await AmmoResolver.PrepareAsync(
            Attack(("ammoItemId", "items/arrows")), Ctx(Weapon(), elsewhere), false, TestContext.Current.CancellationToken);
        Assert.False(fail!.Value.Success);
    }

    [Theory]
    [InlineData("single:1, burst:3, auto:10", 3)]
    [InlineData("single=1, bad", 1)]
    public void ParseModes_ReadsNameCountPairs(string raw, int count) =>
        Assert.Equal(count, AmmoResolver.ParseModes(raw).Count);

    private static Item ItemFromShippedYaml(string file, string holder)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "plugins", "ShadowAndSteel")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = System.IO.Path.Combine(dir!.FullName, "plugins", "ShadowAndSteel", "RulesetData", "dnd5e", "items", file);
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build()
            .Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(path));
        var props = ((Dictionary<object, object>)yaml["properties"]).ToDictionary(kv => (string)kv.Key, kv => (object)kv.Value);
        return new Item
        {
            Id = "items/" + yaml["name"], Name = (string)yaml["name"], HolderId = holder,
            CoreCategory = (string)yaml["category"], Properties = props
        };
    }

    [Fact]
    public async Task ShippedBoltBundle_HoldsTwentyRounds_NotOne_AndDepletes()
    {
        var bolts = ItemFromShippedYaml("ss_crossbow_bolts_20.yaml", "chars/archer");
        var crossbow = new Item
        {
            Id = "items/xbow", Name = "Crossbow, light", HolderId = "chars/archer", CoreCategory = ItemCategories.Weapon,
            Properties = new() { ["ammoType"] = "bolt", ["fireModes"] = "single:1, burst:3" }
        };
        var action = Attack(("weaponItemId", "items/xbow"), ("mode", "burst"));
        var (fail, plan) = await AmmoResolver.PrepareAsync(action, Ctx(crossbow, bolts), false, TestContext.Current.CancellationToken);

        Assert.Null(fail);
        Assert.Equal(20, plan!.Rounds);
        Assert.Equal(3, plan.Spend);
        Assert.Contains("20 → 17 bolts", AmmoResolver.Spend(plan));
        Assert.Equal(20, bolts.MaxCharges);
        Assert.Equal(17, bolts.CurrentCharges);
    }

    [Fact]
    public async Task ShippedFireArrow_IsTheRider_ForAShortbow()
    {
        var arrow = ItemFromShippedYaml("ss_fire_arrow.yaml", "chars/archer");
        var action = Attack();
        var (fail, plan) = await AmmoResolver.PrepareAsync(action, Ctx(Weapon(), arrow), false, TestContext.Current.CancellationToken);
        Assert.Null(fail);
        Assert.Equal(1, plan!.Rounds);
        Assert.Equal("fire", action.Parameters["riderType"]);
    }
}
