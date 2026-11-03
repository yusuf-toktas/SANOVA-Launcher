using Sanova.Core.IO;

namespace Sanova.Core.Installation;

public enum VerifyMode
{
    /// <summary>Size and write time; files whose write time changed are hashed.</summary>
    Quick,

    /// <summary>SHA-256 of every managed file.</summary>
    Full,
}

public enum VerificationProblem
{
    Missing,
    Modified,
}

public sealed record VerificationIssue(string Path, VerificationProblem Problem, FileOrigin Origin, string? Component);

public sealed record VerificationReport(int Checked, IReadOnlyList<VerificationIssue> Issues, DateTimeOffset CheckedAt, VerifyMode Mode)
{
    public bool Passed => Issues.Count == 0;

    public IReadOnlyList<string> DamagedPaths => Issues.Select(i => i.Path).ToList();
}

/// <summary>Checks managed files against the hashes recorded when they were installed.</summary>
public static class InstallVerifier
{
    public static async Task<VerificationReport> VerifyAsync(string installDirectory, InstallState state, VerifyMode mode,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var issues = new List<VerificationIssue>();
        var files = state.Files.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        var total = mode == VerifyMode.Full ? files.Sum(f => f.Value.Size) : 0;
        long done = 0;
        var index = 0;

        foreach (var (path, record) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var full = SafePath.GetFullPathUnder(installDirectory, path);
            var info = new FileInfo(full);

            if (!info.Exists)
            {
                issues.Add(new VerificationIssue(path, VerificationProblem.Missing, record.Origin, record.Component));
            }
            else if (info.Length != record.Size)
            {
                issues.Add(new VerificationIssue(path, VerificationProblem.Modified, record.Origin, record.Component));
            }
            else if (mode == VerifyMode.Full || record.LastWriteUtc is null || info.LastWriteTimeUtc != record.LastWriteUtc)
            {
                var baseline = done;
                var reporter = mode == VerifyMode.Full
                    ? new InlineProgress<long>(bytes =>
                    {
                        baseline += bytes;
                        progress?.Report(new OperationProgress(OperationStage.Verifying, baseline, total, index, files.Count, path));
                    })
                    : null;
                var actual = await FileHashing.Sha256FileAsync(full, reporter, cancellationToken).ConfigureAwait(false);
                if (!FileHashing.HashEquals(actual, record.Sha256))
                {
                    issues.Add(new VerificationIssue(path, VerificationProblem.Modified, record.Origin, record.Component));
                }
            }

            if (mode == VerifyMode.Full) done += record.Size;
            if (mode == VerifyMode.Quick || index % 16 == 0)
            {
                progress?.Report(new OperationProgress(OperationStage.Verifying, done, total, index, files.Count, path));
            }
        }

        return new VerificationReport(files.Count, issues, DateTimeOffset.UtcNow, mode);
    }
}
