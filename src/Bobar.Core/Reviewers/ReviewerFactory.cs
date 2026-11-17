using Bobar.Core.Loop;
using Bobar.Core.Model;

namespace Bobar.Core.Reviewers;

public static class ReviewerFactory
{
    public static IReviewer Create(ReviewerContext c) => c.Config.Kind switch
    {
        ReviewerKinds.Fake => FakeReviewer.FromEnvironment(),
        _ => throw new ConfigException($"reviewer kind '{c.Config.Kind}' is not supported yet"),
    };
}
