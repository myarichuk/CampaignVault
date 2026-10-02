using System;
using System.IO;
using System.Linq;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>A later choice with no options of its own inside a subclass ("two more maneuvers") offers the subclass's list.</summary>
public class NestedChoiceOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-nested-choice-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void LaterSubclassChoice_BorrowsTheSubclassesEarlierOptions_AndItsPickCounts()
    {
        var dir = Path.Combine(_root, "testsys", "progressions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "duelist.yaml"), """
            name: duelist
            system: testsys
            levels:
              3:
                features:
                  - name: School
                    choices:
                      subclass:
                        type: Enum
                        options:
                          - id: tactician
                            label: Tactician
                            features:
                              3:
                                - name: Gambits
                                  choices:
                                    gambit:
                                      type: Enum
                                      count: 2
                                      options:
                                        - { id: feint, label: Feint, effects: [{ kind: attackBonus, value: 1 }] }
                                        - { id: riposte, label: Riposte }
                                        - { id: trip, label: Trip }
                              7:
                                - name: More Gambits
                                  choices:
                                    gambit: { type: Enum, count: 1 }
            """);
        var progression = new ProgressionDefinitionProvider(_root, typeof(ProgressionDefinitionProvider).Assembly)
            .GetProgressionsForSystem("testsys")["duelist"];
        Func<int, string, System.Collections.Generic.IEnumerable<string>> picked = (level, key) => (level, key) switch
        {
            (3, "subclass") => ["tactician"],
            (3, "gambit") => ["riposte", "trip"],
            (7, "gambit") => ["feint"],
            _ => [],
        };

        var later = progression.ChoicesUpTo(7, picked).Single(c => c is { Level: 7, Choice.Key: "gambit" });

        Assert.Equal(["feint", "riposte", "trip"], progression.OptionsFor(later.Choice, later.From).Select(o => o.Id));
        Assert.Contains(progression.PickedOptions(7, picked), o => o.Id == "feint");
    }
}
