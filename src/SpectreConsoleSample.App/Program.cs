using System.ComponentModel;
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
        AnsiConsole.Render(
            new FigletText("Migration Tool")
                .LeftAligned()
                .Color(Color.Teal));

        var username = AskUsernameIfMissing(settings.Username);
        var password = AskPasswordIfMissing(settings.Password);
        var environment = AskEnvironmentIfMissing(settings.Environment);

        AnsiConsole.Render(
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
                            case MigrationResult.Fail fail:
                                ++failures;
                                // TODO: use error information for something
                                break;
                        }

                        migrationTask.Increment(1);
                    }

                    return (successes, failures);
                });

            AnsiConsole.Render(
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
                    async _ => await migrator.DisposeAsync());
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

        static string AskPasswordIfMissing(string? current)
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

        await AnsiConsole
            .Live(Text.Empty)
            .StartAsync(async ctx =>
            {
                using var gif = await Image.LoadAsync("rollback.gif", new GifDecoder());
                var metadata = gif.Frames.RootFrame.Metadata.GetGifMetadata();

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
                            ctx.UpdateTarget(canvasImage);

                            var delay = TimeSpan.FromMilliseconds(Math.Max(75, metadata.FrameDelay));
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
        public Fail(int thingId, string reason) : base(thingId)
            => Reason = reason;

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
            await Task.Delay(TimeSpan.FromMilliseconds(1));

            MigrationResult result = (i + random.Next(0, 99)) % 5 != 0
                ? new MigrationResult.Success(i)
                : new MigrationResult.Fail(i, "Random failure");

            yield return result;
        }
    }

    public ValueTask DisposeAsync()
        => new(Task.Delay(TimeSpan.FromSeconds(1)));
}