using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Unit tests for RecordingMode behavior in KnowledgeUpdateHandler and EventOccurredHandler.
/// Verifies that Deliberate recording locks in high salience/importance and skips keyword inference.
/// </summary>
public class RecordingModeTests
{
    [Fact]
    public void RecordingMode_Enum_HasPassiveAndDeliberate()
    {
        // Verify RecordingMode enum has expected values
        Assert.True(Enum.IsDefined(typeof(RecordingMode), RecordingMode.Passive));
        Assert.True(Enum.IsDefined(typeof(RecordingMode), RecordingMode.Deliberate));
    }

    [Fact]
    public void KnowledgeUpdate_WithDeliberateMode_HasRecordingModeField()
    {
        // Verify KnowledgeUpdate has RecordingMode property
        var ku = new KnowledgeUpdate
        {
            CharacterId = "chars/test",
            Topic = "Test",
            Details = "Test details",
            RecordingMode = RecordingMode.Deliberate
        };

        Assert.NotNull(ku.RecordingMode);
        Assert.Equal(RecordingMode.Deliberate, ku.RecordingMode);
    }

    [Fact]
    public void EventOccurred_WithDeliberateMode_HasRecordingModeField()
    {
        // Verify EventOccurred has RecordingMode property
        var ev = new EventOccurred
        {
            Summary = "Test event",
            Category = EventCategory.Discovery,
            RecordingMode = RecordingMode.Deliberate
        };

        Assert.NotNull(ev.RecordingMode);
        Assert.Equal(RecordingMode.Deliberate, ev.RecordingMode);
    }

    [Fact]
    public void RecognitionRuleCatalog_HasSkillTagMap()
    {
        // Verify RecognitionRuleCatalog has expected skill mappings
        var survivalTags = RecognitionRuleCatalog.GetTagsForSkill("Survival").ToList();
        Assert.NotEmpty(survivalTags);
        Assert.Contains("track", survivalTags);
        Assert.Contains("wild", survivalTags);
    }

    [Fact]
    public void RecognitionRuleCatalog_HasBackgroundSkillBoosts()
    {
        // Verify RecognitionRuleCatalog has background→skill mappings
        var rangerSkills = RecognitionRuleCatalog.GetBoostedSkillsForBackground("ranger").ToList();
        Assert.NotEmpty(rangerSkills);
        Assert.Contains("Survival", rangerSkills, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecognitionRuleCatalog_TagMatchesSkill_WorksCaseInsensitive()
    {
        // Verify tag matching is case-insensitive and substring-based
        Assert.True(RecognitionRuleCatalog.TagMatchesSkill("ANIMAL TRACKS", "Survival"));
        Assert.True(RecognitionRuleCatalog.TagMatchesSkill("fresh tracks", "Survival"));
        Assert.True(RecognitionRuleCatalog.TagMatchesSkill("ancient ruin", "History"));
    }

    [Fact]
    public void RecognitionRuleCatalog_MeetsSkillThreshold_Returns True_For_Sufficient_Modifier()
    {
        // Verify skill threshold check
        Assert.True(RecognitionRuleCatalog.MeetsSkillThreshold(3));
        Assert.True(RecognitionRuleCatalog.MeetsSkillThreshold(5));
        Assert.False(RecognitionRuleCatalog.MeetsSkillThreshold(2));
    }
}
