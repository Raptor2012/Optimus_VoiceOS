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

    // The vocabulary was once exact-phrase only, so "please send" and "cancel that" were
    // rejected. Dogfooding showed that made the approval step unusable: replies echo the spoken
    // question rather than reciting a keyword. Those now classify, and the cases below are the
    // ones that must still be refused — no vocabulary word, or a word withholding approval.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ... ")]
    [InlineData("maybe")]
    [InlineData("wait")]
    [InlineData("not sure")]
    public void Classify_RejectsAnythingOutsideTheFiniteVocabulary(string? transcript) =>
        Assert.Equal(ApprovalCommand.Unknown, ApprovalCommandClassifier.Classify(transcript));

    [Theory]
    // People echo the question rather than reciting a keyword.
    [InlineData("send this to Claude")]
    [InlineData("yeah send it")]
    [InlineData("okay send")]
    [InlineData("Sent.")]
    [InlineData("yes please send it to Antigravity")]
    [InlineData("go ahead")]
    public void NaturalApprovals_AreUnderstood(string spoken) =>
        Assert.Equal(ApprovalCommand.Affirmative, ApprovalCommandClassifier.Classify(spoken));

    [Theory]
    // Every one of these contains an approval word and must still never approve.
    [InlineData("don't send that")]
    [InlineData("do not send this to Claude")]
    [InlineData("no, don't send it")]
    [InlineData("wait, don't send")]
    [InlineData("not yet")]
    [InlineData("hold on")]
    public void RefusalsContainingApprovalWords_NeverApprove(string spoken) =>
        Assert.NotEqual(ApprovalCommand.Affirmative, ApprovalCommandClassifier.Classify(spoken));

    [Theory]
    [InlineData("cancel that", ApprovalCommand.Cancel)]
    [InlineData("no cancel it", ApprovalCommand.Cancel)]
    [InlineData("let me say that again", ApprovalCommand.Redictate)]
    [InlineData("use the original please", ApprovalCommand.UseOriginal)]
    public void OtherRepliesKeepTheirMeaning(string spoken, ApprovalCommand expected) =>
        Assert.Equal(expected, ApprovalCommandClassifier.Classify(spoken));

    [Theory]
    // An approval word buried mid-sentence is not an approval. The recoverable outcomes are
    // allowed to match loosely; only approval has to lead the reply.
    [InlineData("add a send button to the page")]
    [InlineData("what time is it")]
    [InlineData("add a retry to fetchUser")]
    [InlineData("")]
    public void SpeechThatIsNotAnApproval_NeverApproves(string spoken) =>
        Assert.NotEqual(ApprovalCommand.Affirmative, ApprovalCommandClassifier.Classify(spoken));
}
