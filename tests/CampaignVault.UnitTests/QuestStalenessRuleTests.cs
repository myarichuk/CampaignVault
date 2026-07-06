using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class QuestStalenessRuleTests
{
    private readonly QuestStalenessRule _sut;

    public QuestStalenessRuleTests()
    {
        _sut = new QuestStalenessRule();
    }

    [Fact]
    public async Task ApplyAsync_PastDeadline_FailsQuestAndEmitsRumorAndEvent()
    {
        // Arrange
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 15 },
            new System.Collections.Generic.List<Rumor>(),
            new System.Collections.Generic.List<Character>(),
            null!,
            1,
            "test-camp",
            null,
            new System.Collections.Generic.List<Quest>
            {
                new Quest
                {
                    Id = "quests/test_1",
                    Title = "Test Quest",
                    DeadlineDay = 10,
                    OverallState = QuestState.InProgress,
                    Objectives = [new QuestObjective("Obj 1", QuestState.InProgress)]
                }
            }
        );

        // Act
        var result = await _sut.ApplyAsync(context);

        // Assert
        Assert.Contains(result.NarrativeEvents, n => n.Contains("failed because its deadline"));
        Assert.Equal(3, result.Deltas.Count);
        
        var progressDelta = result.Deltas.OfType<QuestProgress>().FirstOrDefault();
        Assert.NotNull(progressDelta);
        Assert.Equal("quests/test_1", progressDelta.QuestId);
        Assert.Equal(QuestState.Failed, progressDelta.NewState);

        var eventDelta = result.Deltas.OfType<EventOccurred>().FirstOrDefault();
        Assert.NotNull(eventDelta);
        Assert.Contains("The deadline for 'Test Quest' has passed", eventDelta.Summary);

        var rumorDelta = result.Deltas.OfType<RumorCreate>().FirstOrDefault();
        Assert.NotNull(rumorDelta);
        Assert.Contains("Test Quest", rumorDelta.Subject);
    }

    [Fact]
    public async Task ApplyAsync_BeforeDeadline_IsSilent()
    {
        // Arrange
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 5 },
            new System.Collections.Generic.List<Rumor>(),
            new System.Collections.Generic.List<Character>(),
            null!,
            1,
            "test-camp",
            null,
            new System.Collections.Generic.List<Quest>
            {
                new Quest
                {
                    Id = "quests/test_2",
                    Title = "Test Quest 2",
                    DeadlineDay = 10,
                    OverallState = QuestState.Open,
                    Objectives = [new QuestObjective("Obj 1", QuestState.Open, DayStarted: 1)]
                }
            }
        );

        // Act
        var result = await _sut.ApplyAsync(context);

        // Assert
        Assert.Empty(result.NarrativeEvents);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public async Task ApplyAsync_OldQuestWithoutDeadline_EmitsNagWarning()
    {
        // Arrange
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 15 },
            new System.Collections.Generic.List<Rumor>(),
            new System.Collections.Generic.List<Character>(),
            null!,
            1,
            "test-camp",
            null,
            new System.Collections.Generic.List<Quest>
            {
                new Quest
                {
                    Id = "quests/test_3",
                    Title = "Test Quest 3",
                    OverallState = QuestState.InProgress,
                    Objectives = [new QuestObjective("Obj 1", QuestState.InProgress, DayStarted: 2)]
                }
            }
        );

        // Act
        var result = await _sut.ApplyAsync(context);

        // Assert
        Assert.Contains(result.NarrativeEvents, n => n.Contains("pending for over 10 days"));
        Assert.Empty(result.Deltas);
    }
}
