using Optimus.Core.Voice;
using Xunit;

namespace Optimus.Core.Tests;

public sealed class ApprovalCommandClassifierTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("YEAH")]
    [InlineData(" yep! ")]
    [InlineData("confirm.")]
    [InlineData("send")]
    [InlineData("send, it")]
    [InlineData("go   ahead")]
    [InlineData("do-it")]
    public void Classify_AcceptsEveryAffirmativeVariant(string transcript) =>
        Assert.Equal(ApprovalCommand.Affirmative, ApprovalCommandClassifier.Classify(transcript));

    [Theory]
    [InlineData("redictate")]
    [InlineData("Try again!")]
    [InlineData("start-over")]
    [InlineData("redo")]
    [InlineData("retry")]
    [InlineData("change that")]
    public void Classify_AcceptsEveryRedictationVariant(string transcript) =>
        Assert.Equal(ApprovalCommand.Redictate, ApprovalCommandClassifier.Classify(transcript));

    [Theory]
    [InlineData("cancel")]
    [InlineData(" CANCEL!!! ")]
    [InlineData("no")]
    [InlineData("NO!")]
    [InlineData("stop")]
    [InlineData("abort")]
    [InlineData("scratch that")]
    [InlineData("never mind")]
    public void Classify_AcceptsEveryCancelVariant(string transcript) =>
        Assert.Equal(ApprovalCommand.Cancel, ApprovalCommandClassifier.Classify(transcript));

    [Theory]
    [InlineData("use original")]
    [InlineData("original")]
    [InlineData("USE RAW")]
    [InlineData("raw.")]
    [InlineData("keep original")]
    [InlineData("revert to original")]
    [InlineData("revert")]
    public void Classify_AcceptsEveryUseOriginalVariant(string transcript) =>
        Assert.Equal(ApprovalCommand.UseOriginal, ApprovalCommandClassifier.Classify(transcript));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ... ")]
    [InlineData("please send")]
    [InlineData("yes send")]
    [InlineData("confirm it")]
    [InlineData("do it now")]
    [InlineData("cancel that")]
    [InlineData("maybe")]
    [InlineData("wait")]
    public void Classify_RejectsAnythingOutsideTheFiniteVocabulary(string? transcript) =>
        Assert.Equal(ApprovalCommand.Unknown, ApprovalCommandClassifier.Classify(transcript));
}
