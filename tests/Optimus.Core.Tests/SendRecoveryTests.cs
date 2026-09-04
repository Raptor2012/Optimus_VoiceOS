namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers;
using Optimus.Providers.Windows;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

public sealed class SendRecoveryTests
{
    private static readonly WindowCandidate Candidate =
        new(42, 1234, "claude", "Claude", "TestWindow");

    [Fact]
    public async Task FailedSend_KeepsDraftVisibleEditableAndRetryable()
    {
        var adapter = new RecoveryAdapter { NextResult = SendStatus.FocusFailed };
        adapter.Bind(Candidate);
        using var viewModel = BuildViewModel(adapter);
        viewModel.DraftText = "Keep this exact prompt.";
        viewModel.State = WidgetState.Confirm;

        await viewModel.ConfirmAsync();

        Assert.Equal(WidgetState.Error, viewModel.State);
        Assert.Equal("Keep this exact prompt.", viewModel.DraftText);
        Assert.True(viewModel.IsDraftVisible);
        Assert.True(viewModel.IsConfirmPanelVisible);
        Assert.True(viewModel.IsDraftEditable);

        adapter.NextResult = SendStatus.Sent;
        await viewModel.ConfirmAsync();

        Assert.Equal(WidgetState.Sent, viewModel.State);
        Assert.Equal("Keep this exact prompt.", viewModel.LastSentText);
        Assert.Equal(2, adapter.SendCount);
    }

    [Fact]
    public async Task AdapterException_DoesNotLeaveWidgetStuckSending()
    {
        var adapter = new RecoveryAdapter { ThrowOnSend = true };
        adapter.Bind(Candidate);
        using var viewModel = BuildViewModel(adapter);
        viewModel.DraftText = "Retry me after the exception.";
        viewModel.State = WidgetState.Confirm;

        await viewModel.ConfirmAsync();

        Assert.Equal(WidgetState.Error, viewModel.State);
        Assert.True(viewModel.IsDraftEditable);
        Assert.True(viewModel.IsDraftVisible);
        Assert.True(viewModel.IsConfirmPanelVisible);
        Assert.Equal("Retry me after the exception.", viewModel.DraftText);
        Assert.Contains("simulated adapter failure", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotReadyThenExplicitBind_CanSendExistingDraft()
    {
        var adapter = new RecoveryAdapter();
        using var viewModel = BuildViewModel(adapter);
        viewModel.DraftText = "Bind, then send this prompt.";
        viewModel.State = WidgetState.Confirm;

        await viewModel.ConfirmAsync();

        Assert.Equal(WidgetState.Error, viewModel.State);
        Assert.Equal(Candidate, viewModel.SelectedWindowChoice);
        Assert.True(viewModel.IsConfirmPanelVisible);

        viewModel.BindSelectedWindow();

        Assert.Equal(WidgetState.Confirm, viewModel.State);
        Assert.Equal("Bind, then send this prompt.", viewModel.DraftText);

        await viewModel.ConfirmAsync();

        Assert.Equal(WidgetState.Sent, viewModel.State);
        Assert.Equal("Bind, then send this prompt.", viewModel.LastSentText);
    }

    private static WidgetViewModel BuildViewModel(RecoveryAdapter adapter)
    {
        var viewModel = new WidgetViewModel(action => action());
        viewModel.AttachDestinations(new DestinationRegistry(new[] { adapter }));
        viewModel.SelectedDestination = viewModel.Destinations[0];
        return viewModel;
    }

    private sealed class RecoveryAdapter : IDestinationAdapter
    {
        private WindowCandidate? _bound;

        public string DestinationId => "claude";

        public string DisplayName => "Claude";

        public string ProcessName => "claude";

        public WindowCandidate? BoundWindow => _bound;

        public SendStatus NextResult { get; set; } = SendStatus.Sent;

        public bool ThrowOnSend { get; set; }

        public int SendCount { get; private set; }

        public DestinationStatus Probe()
        {
            IReadOnlyList<WindowCandidate> candidates = new[] { Candidate };
            return _bound == null
                ? new DestinationStatus(DestinationReadiness.NotBound, candidates, null, "not bound")
                : new DestinationStatus(DestinationReadiness.Ready, candidates, _bound, "ready");
        }

        public void Bind(WindowCandidate candidate) => _bound = candidate;

        public void Unbind() => _bound = null;

        public Task<SendResult> SendAsync(
            ConfirmedDraft draft,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            if (ThrowOnSend)
            {
                throw new UnauthorizedAccessException("simulated adapter failure");
            }

            return Task.FromResult(new SendResult(NextResult, NextResult.ToString(), 1));
        }
    }
}
