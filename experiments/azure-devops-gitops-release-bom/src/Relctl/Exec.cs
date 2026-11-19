using System.Diagnostics;
using System.Text;

namespace Relctl;

/// <summary>A command to run. ReadOnly commands (az show, git show) still run in a dry run.</summary>
public sealed record Command(string File, IReadOnlyList<string> Args)
{
    public bool ReadOnly { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Don't echo output as it arrives (for commands whose output is parsed).</summary>
    public bool Quiet { get; init; }

    public override string ToString() =>
        string.Join(' ', new[] { File }.Concat(Args.Select(a => a.Contains(' ') || a.Length == 0 ? $"'{a}'" : a)));
}

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public interface ICommandRunner
{
    CommandResult Run(Command command);
}

public sealed class ProcessRunner : ICommandRunner
{
    public static readonly ProcessRunner Instance = new();

    public CommandResult Run(Command command)
    {
        var psi = new ProcessStartInfo(command.File)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = command.WorkingDirectory ?? "",
        };
        foreach (var a in command.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in command.Environment) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (stdout) stdout.AppendLine(e.Data); if (!command.Quiet) Console.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (stderr) stderr.AppendLine(e.Data); if (!command.Quiet) Console.Error.WriteLine(e.Data); };
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new CommandResult(127, "", $"Couldn't start {command.File}: {e.Message}");
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(command.Timeout))
        {
            p.Kill(entireProcessTree: true);
            return new CommandResult(124, stdout.ToString(), $"{stderr}Timed out after {command.Timeout.TotalSeconds:0}s.");
        }
        p.WaitForExit(); // flush the async readers
        return new CommandResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>Runs read-only commands for real and prints everything else instead of running it.</summary>
public sealed class DryRunRunner(ICommandRunner inner) : ICommandRunner
{
    public CommandResult Run(Command command)
    {
        if (command.ReadOnly) return inner.Run(command);
        Log.Info($"[dry run] would run: {command}");
        return new CommandResult(0, "", "");
    }
}

/// <summary>
/// Console output that becomes collapsible groups and run annotations in Azure Pipelines
/// (detected by TF_BUILD), and stays plain everywhere else.
/// </summary>
public static class Log
{
    public static bool InPipeline => System.Environment.GetEnvironmentVariable("TF_BUILD") is not null;

    public static void Info(string message) => Console.WriteLine(message);

    public static void Group(string title) => Console.WriteLine(InPipeline ? $"##[group]{title}" : $"── {title}");
    public static void EndGroup() { if (InPipeline) Console.WriteLine("##[endgroup]"); }

    public static void Ok(string message) => Console.WriteLine($"✓ {message}");

    public static void Warn(string message)
    {
        if (InPipeline) Console.WriteLine($"##vso[task.logissue type=warning]{OneLine(message)}");
        else Console.WriteLine($"! {message}");
    }

    public static void Error(string message)
    {
        if (InPipeline) Console.WriteLine($"##vso[task.logissue type=error]{OneLine(message)}");
        Console.Error.WriteLine($"✗ {message}");
    }

    /// <summary>Set a pipeline output variable, readable by later stages.</summary>
    public static void SetOutput(string name, string value) =>
        Console.WriteLine($"##vso[task.setvariable variable={name};isOutput=true]{OneLine(value)}");

    public static void UploadSummary(string path) => Console.WriteLine($"##vso[task.uploadsummary]{path}");

    /// <summary>Logging commands are one line each; a newline would end the command early.</summary>
    public static string OneLine(string s) => s.ReplaceLineEndings(" ").Replace("  ", " ");
}
