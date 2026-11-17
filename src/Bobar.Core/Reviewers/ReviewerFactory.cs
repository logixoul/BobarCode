using Bobar.Core.Loop;
using Bobar.Core.Model;

namespace Bobar.Core.Reviewers;

public static class ReviewerFactory
{
    public static IReviewer Create(ReviewerContext c) => c.Config.Kind switch
    {
        ReviewerKinds.Pi => new PiReviewer(c),
        ReviewerKinds.Claude => new ClaudeReviewer(c),
        ReviewerKinds.Fake => FakeReviewer.FromEnvironment(),
        _ => throw new ConfigException($"unknown reviewer kind '{c.Config.Kind}'"),
    };
}
