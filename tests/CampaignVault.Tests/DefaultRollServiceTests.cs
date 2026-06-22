using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class DefaultRollServiceTests
{
    private const int Seed = 42;

    [Fact]
    public async Task RollAsync_Standard_CalculatesCorrectly()
    {
        // Arrange
        var testRng = new Random(Seed);
        var serviceRng = new Random(Seed);
        var service = new DefaultRollService(serviceRng);

        var request = new RollRequest
        {
            Tag = "std_test",
            Expression = "2d10+3",
            Mechanic = DiceMechanic.Standard,
            Bonus = 2
        };

        // Act
        var outcome = await service.RollAsync(request);

        // Assert
        var expectedDice = new List<int> { testRng.Next(1, 11), testRng.Next(1, 11) };
        var expectedTotal = expectedDice.Sum() + 3 + 2;

        Assert.Equal("std_test", outcome.Tag);
        Assert.Equal(expectedTotal, outcome.Result);
        Assert.Equal(expectedDice, outcome.IndividualDice);
        Assert.False(outcome.HasCritical);
        Assert.False(outcome.HasComplication);
        Assert.Contains("[", outcome.Summary);
    }

    [Fact]
    public async Task RollAsync_Standard_DetectsCriticalAndComplicationOnD20()
    {
        // Test Critical (rolling 20 on d20) and Complication (rolling 1 on d20)
        // Find a seed that produces a critical (20) on 1d20
        var critSeed = 0;
        while (true)
        {
            var testRng = new Random(critSeed);
            if (testRng.Next(1, 21) == 20)
            {
                break;
            }

            critSeed++;
        }

        var outcomeCrit = await new DefaultRollService(new Random(critSeed)).RollAsync(new RollRequest
        {
            Expression = "1d20",
            Mechanic = DiceMechanic.Standard
        });
        Assert.True(outcomeCrit.HasCritical);
        Assert.False(outcomeCrit.HasComplication);

        // Find a seed that produces a complication (1) on 1d20
        var compSeed = 0;
        while (true)
        {
            var testRng = new Random(compSeed);
            if (testRng.Next(1, 21) == 1)
            {
                break;
            }

            compSeed++;
        }

        var outcomeComp = await new DefaultRollService(new Random(compSeed)).RollAsync(new RollRequest
        {
            Expression = "1d20",
            Mechanic = DiceMechanic.Standard
        });
        Assert.True(outcomeComp.HasComplication);
        Assert.False(outcomeCrit.HasComplication);
    }

    [Fact]
    public async Task RollAsync_Advantage_KeepsHighestSet()
    {
        // Arrange
        var testRng = new Random(Seed);
        var serviceRng = new Random(Seed);
        var service = new DefaultRollService(serviceRng);

        var request = new RollRequest
        {
            Tag = "adv_test",
            Expression = "2d6+1",
            Mechanic = DiceMechanic.Advantage,
            Bonus = 1
        };

        // Act
        var outcome = await service.RollAsync(request);

        // Assert
        // First set
        var first = new List<int> { testRng.Next(1, 7), testRng.Next(1, 7) };
        // Second set
        var second = new List<int> { testRng.Next(1, 7), testRng.Next(1, 7) };

        var expectedKept = first.Sum() >= second.Sum() ? first : second;
        var expectedTotal = expectedKept.Sum() + 1 + 1;

        Assert.Equal(expectedTotal, outcome.Result);
        Assert.Equal(expectedKept, outcome.IndividualDice);
        Assert.Contains("Advantage", outcome.Summary);
    }

    [Fact]
    public async Task RollAsync_Disadvantage_KeepsLowestSet()
    {
        // Arrange
        var testRng = new Random(Seed);
        var serviceRng = new Random(Seed);
        var service = new DefaultRollService(serviceRng);

        var request = new RollRequest
        {
            Tag = "disadv_test",
            Expression = "2d6+1",
            Mechanic = DiceMechanic.Disadvantage,
            Bonus = 1
        };

        // Act
        var outcome = await service.RollAsync(request);

        // Assert
        // First set
        var first = new List<int> { testRng.Next(1, 7), testRng.Next(1, 7) };
        // Second set
        var second = new List<int> { testRng.Next(1, 7), testRng.Next(1, 7) };

        var expectedKept = first.Sum() <= second.Sum() ? first : second;
        var expectedTotal = expectedKept.Sum() + 1 + 1;

        Assert.Equal(expectedTotal, outcome.Result);
        Assert.Equal(expectedKept, outcome.IndividualDice);
        Assert.Contains("Disadvantage", outcome.Summary);
    }

    [Fact]
    public async Task RollAsync_Explosive_ChainsOnMax()
    {
        // Arrange
        // Let's find a seed that produces an explosive roll (rolling a max face first)
        var explosiveSeed = 0;
        while (true)
        {
            var testRng = new Random(explosiveSeed);
            if (testRng.Next(1, 5) == 4)
            {
                break;
            }

            explosiveSeed++;
        }

        var testRngVerify = new Random(explosiveSeed);
        var serviceRng = new Random(explosiveSeed);
        var service = new DefaultRollService(serviceRng);

        var request = new RollRequest
        {
            Expression = "1d4",
            Mechanic = DiceMechanic.Explosive
        };

        // Act
        var outcome = await service.RollAsync(request);

        // Assert
        var expectedDice = new List<int>();
        var expectedTotal = 0;
        int roll;
        do
        {
            roll = testRngVerify.Next(1, 5);
            expectedDice.Add(roll);
            expectedTotal += roll;
        } while (roll == 4);

        Assert.Equal(expectedTotal, outcome.Result);
        Assert.Equal(expectedDice, outcome.IndividualDice);
        Assert.True(outcome.HasCritical);
        Assert.Contains("(chained)", outcome.Summary);
    }

    [Fact]
    public async Task RollAsync_KeepHighest_KeepsKHighest()
    {
        // Arrange
        var testRng = new Random(Seed);
        var serviceRng = new Random(Seed);
        var service = new DefaultRollService(serviceRng);

        var request = new RollRequest
        {
            Expression = "4d6",
            Mechanic = DiceMechanic.KeepHighest,
            Keep = 3
        };

        // Act
        var outcome = await service.RollAsync(request);

        // Assert
        var rolled = Enumerable.Range(0, 4).Select(_ => testRng.Next(1, 7)).ToList();
        var expectedKept = rolled.OrderByDescending(d => d).Take(3).ToList();

        Assert.Equal(expectedKept.Sum(), outcome.Result);
        Assert.Equal(expectedKept, outcome.IndividualDice);
        Assert.Contains("KeepHigh", outcome.Summary);
    }

    [Fact]
    public async Task RollAsync_KeepLowest_KeepsKLowest()
    {
        // Arrange
        var testRng = new Random(Seed);
        var serviceRng = new Random(Seed);
        var service = new DefaultRollService(serviceRng);

        var request = new RollRequest
        {
            Expression = "4d6",
            Mechanic = DiceMechanic.KeepLowest,
            Keep = 2
        };

        // Act
        var outcome = await service.RollAsync(request);

        // Assert
        var rolled = Enumerable.Range(0, 4).Select(_ => testRng.Next(1, 7)).ToList();
        var expectedKept = rolled.OrderBy(d => d).Take(2).ToList();

        Assert.Equal(expectedKept.Sum(), outcome.Result);
        Assert.Equal(expectedKept, outcome.IndividualDice);
        Assert.Contains("KeepLow", outcome.Summary);
    }

    [Fact]
    public async Task RollAsync_RollUnder_EvaluatesCorrectly()
    {
        // Arrange
        var testRng = new Random(Seed);
        var serviceRng = new Random(Seed);
        var service = new DefaultRollService(serviceRng);

        var request = new RollRequest
        {
            Expression = "3d6",
            Mechanic = DiceMechanic.RollUnder,
            TargetNumber = 10
        };

        // Act
        var outcome = await service.RollAsync(request);

        // Assert
        var rolled = Enumerable.Range(0, 3).Select(_ => testRng.Next(1, 7)).ToList();
        var total = rolled.Sum();
        var expectedSuccess = total <= 10;

        Assert.Equal(total, outcome.Result);
        Assert.Equal(expectedSuccess, outcome.IsSuccess);
        Assert.Contains("RollUnder", outcome.Summary);
    }

    [Fact]
    public async Task RollAsync_RollUnder_ThrowsIfNoTargetNumber()
    {
        var service = new DefaultRollService();
        var request = new RollRequest
        {
            Expression = "1d20",
            Mechanic = DiceMechanic.RollUnder
            // TargetNumber omitted
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollAsync(request));
    }

    [Fact]
    public async Task RollAsync_SuccessCount_CountsCorrectly()
    {
        // Arrange
        var testRng = new Random(Seed);
        var serviceRng = new Random(Seed);
        var service = new DefaultRollService(serviceRng);

        var request = new RollRequest
        {
            Expression = "3d20",
            Mechanic = DiceMechanic.SuccessCount,
            TargetNumber = 12,
            CriticalThreshold = 2
        };

        // Act
        var outcome = await service.RollAsync(request);

        // Assert
        var rolled = Enumerable.Range(0, 3).Select(_ => testRng.Next(1, 21)).ToList();
        var expectedSuccesses = 0;
        var expectedComplication = false;
        foreach (var d in rolled)
        {
            if (d <= 12)
            {
                expectedSuccesses += (d <= 2) ? 2 : 1;
            }

            if (d == 20)
            {
                expectedComplication = true;
            }
        }

        Assert.Equal(expectedSuccesses, outcome.Result);
        Assert.Equal(expectedSuccesses, outcome.Successes);
        Assert.Equal(expectedSuccesses > 0, outcome.IsSuccess);
        Assert.Equal(expectedComplication, outcome.HasComplication);
        Assert.Contains("success(es)", outcome.Summary);
    }

    [Fact]
    public async Task RollAsync_SuccessCount_ThrowsIfNoTargetNumber()
    {
        var service = new DefaultRollService();
        var request = new RollRequest
        {
            Expression = "2d20",
            Mechanic = DiceMechanic.SuccessCount
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollAsync(request));
    }

    [Fact]
    public async Task RollFalloutCombatDiceAsync_CalculatesCorrectly()
    {
        // Arrange
        var testRng = new Random(Seed);
        var serviceRng = new Random(Seed);
        var service = new DefaultRollService(serviceRng);

        // Act
        var result = await service.RollFalloutCombatDiceAsync(5);

        // Assert
        var expectedDamage = 0;
        var expectedEffects = 0;
        var expectedCrit = false;

        for (var i = 0; i < 5; i++)
        {
            var face = testRng.Next(1, 7);
            switch (face)
            {
                case 1:
                    expectedDamage++;
                    break;
                case 2:
                    expectedDamage += 2;
                    expectedCrit = true;
                    break;
                case 3:
                case 4:
                    break;
                case 5:
                case 6:
                    expectedDamage++;
                    expectedEffects++;
                    break;
            }
        }

        Assert.Equal(expectedDamage, result.Damage);
        Assert.Equal(expectedEffects, result.Effects);
        Assert.Equal(expectedCrit, result.HasCritical);
    }

    [Fact]
    public async Task RollBatchAsync_EvaluatesInOrder()
    {
        var service = new DefaultRollService();
        var requests = new[]
        {
            new RollRequest { Tag = "t1", Expression = "1d6" },
            new RollRequest { Tag = "t2", Expression = "2d8" }
        };

        var results = await service.RollBatchAsync(requests);

        Assert.Equal(2, results.Count);
        Assert.Equal("t1", results[0].Tag);
        Assert.Equal("t2", results[1].Tag);
    }
}