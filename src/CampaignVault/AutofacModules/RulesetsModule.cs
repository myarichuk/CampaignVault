using Autofac;
using CampaignVault.Data;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.AutofacModules;

public class RulesetsModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        var assembly = typeof(Program).Assembly;

        // Ruleset Modules
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<IRulesetModule>() && !t.IsAbstract)
            .As<IRulesetModule>()
            .InstancePerLifetimeScope();

        builder.RegisterType<DefaultRollService>().As<IRollService>().SingleInstance();
        builder.RegisterType<RulesetModuleSelector>().As<IRulesetModuleSelector>().InstancePerLifetimeScope();
        builder.RegisterType<CharacterBootstrapOrchestrator>().InstancePerLifetimeScope();
    }
}
