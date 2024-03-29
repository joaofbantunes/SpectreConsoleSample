﻿using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp;
using Spectre.Console;
using Spectre.Console.Cli;
using Color = Spectre.Console.Color;

var app = new CommandApp();
app.Configure(config =>
{
    config.AddCommand<MigrateCommand>("migrate");
    config.AddCommand<RollbackCommand>("rollback");
});
return await app.RunAsync(args);

public class MigrateCommand : AsyncCommand<MigrateCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-u|--username")]
        [Description("Username to access the environment for migration")]
        public string? Username { get; init; }

        [CommandOption("-p|--password")]
        [Description("Password to access the environment for migration")]
        public string? Password { get; init; }

        [CommandOption("-e|--environment")]
        [Description("Target environment for the migration")]
        public Environment? Environment { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        AnsiConsole.Write(
            new FigletText("Migration Tool")
                .LeftAligned()
                .Color(Color.Teal));

        var username = AskUsernameIfMissing(settings.Username);
        var password = AskPasswordIfInvalidOrMissing(settings.Password);
        var environment = AskEnvironmentIfMissing(settings.Environment);

        AnsiConsole.Write(
            new Table()
                .AddColumn(new TableColumn("Setting").Centered())
                .AddColumn(new TableColumn("Value").Centered())
                .AddRow("Username", username)
                .AddRow("Password", "REDACTED")
                .AddRow(
                    new Text("Environment"),
                    new Markup($"[{GetEnvironmentColor(environment)}]{environment}[/]")));

        var proceedWithSettings = AnsiConsole.Prompt(
            new SelectionPrompt<bool> { Converter = value => value ? "Yes" : "No" }
                .Title("Proceed with the aforementioned settings?")
                .AddChoices(true, false)
        );

        if (!proceedWithSettings)
        {
            return 0;
        }

        var migrator = new SampleMigrator();

        try
        {
            await AnsiConsole
                .Status()
                .StartAsync(
                    "Connecting...",
                    _ => migrator.ConnectAsync(username, password, environment));

            var migrationInformation = await AnsiConsole
                .Status()
                .Spinner(Spinner.Known.Clock)
                .StartAsync(
                    "Gathering migration information...",
                    _ => migrator.GatherMigrationInformationAsync());

            var proceedWithMigration = AnsiConsole
                .Prompt(
                    new SelectionPrompt<bool> { Converter = value => value ? "Yes" : "No" }
                        .Title($"Found {migrationInformation.NumberOfThingsToMigrate} things to migrate. Proceed?")
                        .AddChoices(true, false));

            if (!proceedWithMigration)
            {
                return 0;
            }

            var migrationResults = await AnsiConsole
                .Progress()
                .StartAsync(async ctx =>
                {
                    var migrationTask = ctx.AddTask(
                        "Migrating...",
                        maxValue: migrationInformation.NumberOfThingsToMigrate);

                    var successes = 0;
                    var failures = 0;

                    await foreach (var migrationResult in migrator.MigrateAsync())
                    {
                        switch (migrationResult)
                        {
                            case MigrationResult.Success:
                                ++successes;
                                break;
                            case MigrationResult.Fail:
                                ++failures;
                                break;
                        }

                        migrationTask.Increment(1);
                    }

                    return (successes, failures);
                });

            AnsiConsole.Write(
                new BarChart()
                    .Label("Migration results")
                    .AddItem("Succeeded", migrationResults.successes, Color.Green)
                    .AddItem("Failed", migrationResults.failures, Color.Red));
        }
        finally
        {
            await AnsiConsole
                .Status()
                .StartAsync(
                    "Disconnecting...",
                    _ => migrator.DisposeAsync().AsTask());
        }

        return 0;

        static string AskUsernameIfMissing(string? current)
            => !string.IsNullOrWhiteSpace(current)
                ? current
                : AnsiConsole.Prompt(
                    new TextPrompt<string>("What's the username?")
                        .Validate(username
                            => !string.IsNullOrWhiteSpace(username)
                                ? ValidationResult.Success()
                                : ValidationResult.Error("[yellow]Invalid username[/]")));

        static string AskPasswordIfInvalidOrMissing(string? current)
            => TryGetValidPassword(current, out var validPassword)
                ? validPassword
                : AnsiConsole.Prompt(
                    new TextPrompt<string>("What's the password?")
                        .Secret()
                        .Validate(password
                            => TryGetValidPassword(password, out _)
                                ? ValidationResult.Success()
                                : ValidationResult.Error("[yellow]Invalid password[/]")));

        static bool TryGetValidPassword(string? password, [NotNullWhen(true)] out string? validPassword)
        {
            var isValidPassword = !string.IsNullOrWhiteSpace(password) && password.Length > 2;
            validPassword = password;
            return isValidPassword;
        }

        static Environment AskEnvironmentIfMissing(Environment? current)
            => current ?? AnsiConsole.Prompt(
                new SelectionPrompt<Environment>()
                    .Title("What's the target environment?")
                    .AddChoices(
                        Environment.Development,
                        Environment.Staging,
                        Environment.Production));

        static string GetEnvironmentColor(Environment environment)
            => environment switch
            {
                Environment.Development => "green",
                Environment.Staging => "yellow",
                Environment.Production => "red",
                _ => throw new ArgumentOutOfRangeException()
            };
    }
}

public class RollbackCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        // as seen on:
        // - https://twitter.com/buhakmeh/status/1417523837076447241
        // - https://github.com/khalidabuhakmeh/AnimatedGifConsole/

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, _) => cts.Cancel();

        var table = new Table()
            .Centered()
            .HideHeaders()
            .NoBorder()
            .AddColumn(new TableColumn(Text.Empty))
            .AddEmptyRow() // gif row
            .AddEmptyRow() // space row
            .AddEmptyRow(); // lyrics row

        await AnsiConsole
            .Live(table)
            .StartAsync(async ctx =>
            {
                var lyrics = new Lyrics();

                using var gif = await Image.LoadAsync("rollback.gif", new GifDecoder());

                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        for (var i = 0; i < gif.Frames.Count; ++i)
                        {
                            await using var memoryStream = new MemoryStream();
                            var frame = gif.Frames.CloneFrame(i);
                            await frame.SaveAsBmpAsync(memoryStream, cts.Token);
                            memoryStream.Position = 0;
                            var canvasImage = new CanvasImage(memoryStream).MaxWidth(32);
                            table.UpdateCell(0, 0, canvasImage);
                            table.UpdateCell(2, 0, lyrics.GetVerse().Centered());
                            ctx.Refresh();

                            var delay = TimeSpan.FromMilliseconds(75);
                            lyrics.Seek(delay);
                            await Task.Delay(delay, cts.Token);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // expected, let it continue and terminate naturally
                    }
                }
            });

        return 0;
    }
}

public enum Environment
{
    Development,
    Staging,
    Production
}

public record MigrationInformation(int NumberOfThingsToMigrate);

public abstract record MigrationResult
{
    private MigrationResult(int thingId) => ThingId = thingId;

    public int ThingId { get; }

    public sealed record Success : MigrationResult
    {
        public Success(int thingId) : base(thingId)
        {
        }
    }

    public sealed record Fail : MigrationResult
    {
        public Fail(int thingId, string reason) : base(thingId) => Reason = reason;

        public string Reason { get; }
    }
}

public class SampleMigrator : IAsyncDisposable
{
    private const int NumberOfThingsToMigrate = 150;

    public Task ConnectAsync(string username, string password, Environment environment)
        => Task.Delay(TimeSpan.FromSeconds(1));

    public async Task<MigrationInformation> GatherMigrationInformationAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        return new MigrationInformation(NumberOfThingsToMigrate);
    }

    public async IAsyncEnumerable<MigrationResult> MigrateAsync()
    {
        var random = new Random();

        for (var i = 0; i < NumberOfThingsToMigrate; ++i)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            MigrationResult result = (i + random.Next(0, 99)) % 5 != 0
                ? new MigrationResult.Success(i)
                : new MigrationResult.Fail(i, "Random failure");

            yield return result;
        }
    }

    public ValueTask DisposeAsync()
        => new(Task.Delay(TimeSpan.FromSeconds(1)));
}

#region surprise

public class Lyrics
{
    private static readonly TimeSpan ElapsedThreshold = TimeSpan.FromSeconds(8);
    private TimeSpan _elapsed = TimeSpan.Zero;

    public void Seek(TimeSpan duration)
    {
        var newElapsed = _elapsed + duration;
        _elapsed = newElapsed > ElapsedThreshold ? TimeSpan.Zero : newElapsed;
    }

    public Text GetVerse()
        => _elapsed switch
        {
            { TotalSeconds: < 1 } => new Text(
                "Never gonna give you up",
                new Style(foreground: Color.White, background: Color.RoyalBlue1)),
            { TotalSeconds: >= 1 and < 2 } => new Text(
                "Never gonna let you down",
                new Style(foreground: Color.White, background: Color.DarkRed)),
            { TotalSeconds: >= 2 and < 3.5 } => new Text(
                "Never gonna run around and desert you",
                new Style(foreground: Color.White, background: Color.Chartreuse4)),
            { TotalSeconds: >= 4 and < 5 } => new Text(
                "Never gonna make you cry",
                new Style(foreground: Color.White, background: Color.Orange4_1)),
            { TotalSeconds: >= 5 and < 6 } => new Text(
                "Never gonna say goodbye",
                new Style(foreground: Color.White, background: Color.DeepSkyBlue4_1)),
            { TotalSeconds: >= 6 and < 7.5 } => new Text(
                "Never gonna tell a lie and hurt you",
                new Style(foreground: Color.White, background: Color.DeepPink4_2)),
            _ => new Text("")
        };
}

#endregion surprise