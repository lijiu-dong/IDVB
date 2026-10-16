using System.Diagnostics;

namespace IDVBuff.Cli;

internal sealed partial class RealCliHost
{
    private async Task<CliCommandResult> ScanAsync(
        string input,
        IReadOnlyList<string> tokens,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var automaticOnly = tokens.Any(token => string.Equals(token, "--automatic", StringComparison.OrdinalIgnoreCase));
        var candidateOption = ParseOption(tokens, "--candidate");
        if (automaticOnly && candidateOption is not null)
            return CliCommandResult.Failed(input, "--automatic cannot be combined with --candidate.",
                stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
        int? candidatePosition = null;
        if (candidateOption is not null
            && (!int.TryParse(candidateOption, out var parsedPosition)
                || parsedPosition < 1))
        {
            return CliCommandResult.Failed(
                input,
                "--candidate 必须是从 1 开始的候选序号。",
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot());
        }
        if (candidateOption is not null)
            candidatePosition = int.Parse(candidateOption);

        if (!await PrepareCaptureTargetAsync(cancellationToken))
        {
            var overlayState = await TryGetOverlayStateAsync(cancellationToken);
            return CliCommandResult.Failed(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(overlayState),
                overlayGame: overlayState);
        }
        var previous = _session.LastRecognition;
        await _session.RunQuickScanAsync(
            new RealCliCandidateSelector(candidatePosition, automaticOnly: automaticOnly));
        var succeeded = _session.LastRecognition is not null
            && !ReferenceEquals(previous, _session.LastRecognition);
        var currentOverlayState = await TryGetOverlayStateAsync(cancellationToken);
        return succeeded
            ? CliCommandResult.Ok(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(currentOverlayState),
                currentOverlayState)
            : CliCommandResult.Failed(
                input,
                _session.StatusMessage,
                stopwatch.Elapsed.TotalMilliseconds,
                BuildSnapshot(currentOverlayState),
                overlayGame: currentOverlayState);
    }
}
