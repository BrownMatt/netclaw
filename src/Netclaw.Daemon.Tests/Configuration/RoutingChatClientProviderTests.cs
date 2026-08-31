// -----------------------------------------------------------------------
// <copyright file="RoutingChatClientProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RoutingChatClientProviderTests
{
    [Fact]
    public void GetClient_caches_per_role_and_shares_Main_and_Fallback()
    {
        var provider = new RoutingChatClientProvider(
            new StubRouter(), NullNotificationSink.Instance, NullLoggerFactory.Instance);

        var main = provider.GetClient(ModelRole.Main);

        Assert.Same(main, provider.GetClient(ModelRole.Main));         // cached per role
        Assert.Same(main, provider.GetClient(ModelRole.Fallback));     // Fallback shares the Main routing client
        Assert.NotSame(main, provider.GetClient(ModelRole.Compaction));// Compaction is its own routing client
        Assert.Same(provider.GetClient(ModelRole.Compaction), provider.GetClient(ModelRole.Compaction));
    }

    [Fact]
    public void GetClient_with_context_and_no_override_reuses_the_role_client()
    {
        var provider = new RoutingChatClientProvider(
            new StubRouter(), NullNotificationSink.Instance, NullLoggerFactory.Instance);

        var byRole = provider.GetClient(ModelRole.Main);
        var byContext = provider.GetClient(new ChatRoutingContext
        {
            Role = ModelRole.Main,
            SessionId = "signalr/s1"
        });

        Assert.Same(byRole, byContext);
    }

    [Fact]
    public void GetClient_with_override_returns_a_context_bound_client()
    {
        var provider = new RoutingChatClientProvider(
            new StubRouter(), NullNotificationSink.Instance, NullLoggerFactory.Instance);

        var overridden = provider.GetClient(new ChatRoutingContext
        {
            Role = ModelRole.Main,
            SessionId = "signalr/s1",
            OverrideModel = new ModelReference { Provider = "p", ModelId = "other" }
        });

        Assert.NotSame(provider.GetClient(ModelRole.Main), overridden);
    }

    private sealed class StubRouter : IChatClientRouter
    {
        private readonly IReadOnlyList<IChatClient> _candidates = [new FakeChatClient()];
        public IReadOnlyList<IChatClient> Route(ChatRoutingContext context) => _candidates;
    }
}

public sealed class OverrideAwareRouterTests
{
    private static ModelSelection Models => new()
    {
        Main = new ModelReference { Provider = "p", ModelId = "main" },
        Fallback = new ModelReference { Provider = "p", ModelId = "fb" },
        Compaction = new ModelReference { Provider = "p", ModelId = "comp" }
    };

    private static OverrideAwareRouter CreateRouter(List<string> created)
    {
        IChatClient Create(ModelReference model)
        {
            created.Add(model.ModelId);
            return new FakeChatClient();
        }

        return new OverrideAwareRouter(new RoleBasedFailoverRouter(Create, Models), Create);
    }

    [Fact]
    public void Override_routes_to_a_single_override_pipeline()
    {
        var created = new List<string>();
        var router = CreateRouter(created);

        var candidates = router.Route(new ChatRoutingContext
        {
            Role = ModelRole.Main,
            SessionId = "signalr/s1",
            OverrideModel = new ModelReference { Provider = "p", ModelId = "override-model" }
        });

        // Single candidate: the configured fallback chain must not apply to
        // an override — a failed override call fails loudly.
        Assert.Single(candidates);
        Assert.Contains("override-model", created);
    }

    [Fact]
    public void Compaction_ignores_the_override()
    {
        var created = new List<string>();
        var router = CreateRouter(created);

        var withOverride = router.Route(new ChatRoutingContext
        {
            Role = ModelRole.Compaction,
            OverrideModel = new ModelReference { Provider = "p", ModelId = "override-model" }
        });
        var without = router.Route(new ChatRoutingContext { Role = ModelRole.Compaction });

        Assert.Same(without, withOverride);
        Assert.DoesNotContain("override-model", created);
    }

    [Fact]
    public void No_override_delegates_unchanged()
    {
        var created = new List<string>();
        var router = CreateRouter(created);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });

        Assert.Equal(2, main.Count); // main + fallback from the wrapped policy
    }

    [Fact]
    public void Repeated_override_reference_reuses_the_memoized_pipeline()
    {
        var created = new List<string>();
        var router = CreateRouter(created);

        // Distinct ModelReference instances with the same provider/model —
        // memoization keys on the values, not the instance.
        var first = router.Route(new ChatRoutingContext
        {
            Role = ModelRole.Main,
            OverrideModel = new ModelReference { Provider = "p", ModelId = "override-model" }
        });
        var second = router.Route(new ChatRoutingContext
        {
            Role = ModelRole.Main,
            OverrideModel = new ModelReference { Provider = "p", ModelId = "override-model" }
        });

        Assert.Same(first[0], second[0]);
        Assert.Equal(1, created.Count(id => id == "override-model"));
    }
}

public sealed class RoleBasedFailoverRouterTests
{
    [Fact]
    public void Main_has_two_candidates_when_fallback_configured()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "p", ModelId = "main" },
            Fallback = new ModelReference { Provider = "p", ModelId = "fb" }
        };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });

        Assert.Equal(2, main.Count);
        Assert.Same(main, router.Route(new ChatRoutingContext { Role = ModelRole.Fallback }));
        // No distinct compaction model → compaction reuses the main candidate list.
        Assert.Same(main, router.Route(new ChatRoutingContext { Role = ModelRole.Compaction }));
    }

    [Fact]
    public void Main_has_one_candidate_when_no_fallback()
    {
        var models = new ModelSelection { Main = new ModelReference { Provider = "p", ModelId = "main" } };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        Assert.Single(router.Route(new ChatRoutingContext { Role = ModelRole.Main }));
    }

    [Fact]
    public void Compaction_is_a_distinct_single_pipeline_when_configured()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "p", ModelId = "main" },
            Compaction = new ModelReference { Provider = "p", ModelId = "comp" }
        };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });
        var compaction = router.Route(new ChatRoutingContext { Role = ModelRole.Compaction });

        Assert.Single(compaction);
        Assert.NotSame(main, compaction);
    }
}
