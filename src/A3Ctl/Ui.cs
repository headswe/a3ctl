using Spectre.Console;
using Spectre.Console.Rendering;

namespace A3Ctl;

// All terminal output. Live displays (progress, spinners, watch) and prompts only appear
// when a person is at a terminal; piped or cron output stays plain, line-based text.
// Messages passed to Ok/Step/Warn/Error/Detail are plain text. Line and Table take Spectre
// markup; Green/Red/... return escaped markup, E() escapes plain text.
public static class Ui
{
    // A3CTL_INTERACTIVE=1 forces live displays, e.g. under terminals Spectre misdetects.
    private static readonly bool Forced = Environment.GetEnvironmentVariable("A3CTL_INTERACTIVE") == "1";

    public static readonly IAnsiConsole Out = Create(Console.Out, Console.IsOutputRedirected);

    public static readonly IAnsiConsole Err = Create(Console.Error, Console.IsErrorRedirected);

    public static bool Interactive { get; } = Forced || (!Console.IsOutputRedirected && Out.Profile.Capabilities.Interactive);

    public static bool CanPrompt => Interactive && !Console.IsInputRedirected;

    public static bool Verbose { get; set; }

    public static bool AssumeYes { get; set; }

    // Piped output gets no escape codes at all and is not wrapped at Spectre's default 80
    // columns, so logs and grep see plain lines.
    private static IAnsiConsole Create(TextWriter writer, bool redirected)
    {
        var plain = redirected && !Forced;
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = plain ? AnsiSupport.No : Forced ? AnsiSupport.Yes : AnsiSupport.Detect,
            ColorSystem = Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 } || plain
                ? ColorSystemSupport.NoColors
                : ColorSystemSupport.Detect,
            Interactive = Forced ? InteractionSupport.Yes : InteractionSupport.Detect,
        });

        if (redirected)
        {
            console.Profile.Width = 400;
        }

        return console;
    }

    public static string E(string text) => Markup.Escape(text);

    public static string Green(string s) => $"[green]{E(s)}[/]";
    public static string Red(string s) => $"[red]{E(s)}[/]";
    public static string Yellow(string s) => $"[yellow]{E(s)}[/]";
    public static string Dim(string s) => $"[dim]{E(s)}[/]";
    public static string Bold(string s) => $"[bold]{E(s)}[/]";

    public static void Line(string markup) => Out.MarkupLine(markup);

    public static void Ok(string message) => Out.MarkupLine($"[green]ok[/]    {E(message)}");
    public static void Step(string message) => Out.MarkupLine($"[bold blue]==>[/]   {E(message)}");
    public static void Detail(string message) => Out.MarkupLine($"[dim]      {E(message)}[/]");
    public static void Warn(string message) => Err.MarkupLine($"[yellow]warn[/]  {E(message)}");
    public static void Error(string message) => Err.MarkupLine($"[red]error[/] {E(message)}");

    public static Table BuildTable(string[] headers, IEnumerable<string[]> rows)
    {
        var table = new Table().Border(Interactive ? TableBorder.Rounded : TableBorder.None);
        if (Interactive)
        {
            table.BorderStyle(new Style(Color.Grey));
        }

        foreach (var header in headers)
        {
            table.AddColumn(new TableColumn($"[bold]{E(header.ToUpperInvariant())}[/]"));
        }

        foreach (var row in rows)
        {
            table.AddRow(row.Select(cell => (IRenderable)new Markup(cell)).ToArray());
        }

        return table;
    }

    public static void Table(string[] headers, IEnumerable<string[]> rows)
    {
        var table = BuildTable(headers, rows);
        Out.Write(table);
        if (table.Rows.Count == 0)
        {
            Line(Dim("(none)"));
        }
    }

    // Asks before something that kicks players or deletes data. Without a terminal (cron,
    // scripts) the command goes ahead, as it would have without the prompt.
    public static bool Confirm(string question)
    {
        if (AssumeYes || !CanPrompt)
        {
            return true;
        }

        return Out.Confirm(question, defaultValue: false);
    }

    public static async Task SpinAsync(string label, Func<Task> action)
    {
        if (!Interactive)
        {
            await action();
            return;
        }

        await Out.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Blue))
            .StartAsync(E(label), _ => action());
    }

    // Runs work with a byte-based progress display, or with null when there is no terminal.
    public static async Task<T> WithProgressAsync<T>(Func<ProgressContext?, Task<T>> work)
    {
        if (!Interactive)
        {
            return await work(null);
        }

        return await Out.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new ElapsedTimeColumn(),
                new SpinnerColumn())
            .StartAsync(ctx => work(ctx));
    }

    public static string Age(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays}d{span.Hours}h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes}m" : $"{span.Minutes}m{span.Seconds}s";

    public static string Date(long unixSeconds) => unixSeconds <= 0
        ? "-"
        : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}
