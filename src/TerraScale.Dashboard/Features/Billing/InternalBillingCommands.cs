using Microsoft.EntityFrameworkCore;
using TerraScale.TerraBase.Contracts;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing.Internal;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Common.Validation;

namespace TerraScale.Dashboard.Features.Billing
{
    // The command entry is also the production Program entry. Complete-application
    // E2E fixtures replace its whole host factory, never individual job functions.
    public static class InternalBillingCommands
    {
        private const string Configure = "configure-internal-billing";
        private const string Report = "report-internal-billing";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static async Task<int> RunAsync(string[] args, Func<string[], WebApplication>? buildApplication = null,
            TextWriter? output = null, TextWriter? error = null, CancellationToken cancellationToken = default)
        {
            output ??= Console.Out;
            error ??= Console.Error;
            buildApplication ??= arguments => DashboardApplication.Build(arguments);
            if (args.Length == 0 || args[0].StartsWith('-'))
            {
                await using var server = buildApplication(args);
                await server.RunAsync(cancellationToken);
                return 0;
            }
            var command = args[0];
            var parsed = Parse(args);
            if (parsed is CommandRejection rejection)
            {
                await error.WriteLineAsync(command + ": " + rejection.Message, cancellationToken);
                return 1;
            }

            var options = parsed switch { Arguments arguments => arguments, CommandRejection => throw new UnreachableException() };

            SessionRecord? grant = null;
            string? failure = null;
            WebApplication? application = null;
            try
            {
                application = buildApplication([]) ?? throw new InvalidOperationException("The command application is unavailable.");
                await using (var db = await application.Services.GetRequiredService<IDbContextFactory<DashboardDbContext>>().CreateDbContextAsync(cancellationToken))
                {
                    await db.Database.MigrateAsync(cancellationToken);
                }

                var auth = application.Services.GetRequiredService<TsAuthGateway>();
                var accounts = application.Services.GetRequiredService<AccountService>();
                grant = await auth.DeviceSignInAsync(output, cancellationToken);
                var context = await accounts.GetContextAsync(grant, cancellationToken);
                if (!context.Session.Email.Equals(options.AdminEmail, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AccountAccessException("Approved account does not match --admin-email.");
                }

                if (await application.Services.GetRequiredService<IPlatformRoleGateway>().PlatformRoleAsync(context.Session, cancellationToken) != "admin")
                {
                    throw new AccountAccessException("Platform administrator authorization is required.");
                }

                var internalBilling = application.Services.GetRequiredService<InternalBillingService>();
                async Task<AccountContext> RefreshAuthorityAsync(CancellationToken token)
                {
                    try { grant = await auth.DeviceRefreshAsync(grant!, token); }
                    catch (SignInRejectedException) { throw new AccountAccessException("Authorization has expired.", 401); }
                    return await accounts.GetContextAsync(grant, token);
                }
                if (options.Command == Configure)
                {
                    await internalBilling.ConfigureAsync(context, new(options.OrganizationId, options.BasePlanKey,
                        options.SpendCapCents, options.WriteCentsPerMillion, options.StorageCentsPerGBMonth), RefreshAuthorityAsync, cancellationToken);
                    output.WriteLine($"configured report-only internal billing for organization {options.OrganizationId} using base plan {options.BasePlanKey}");
                }
                else
                {
                    var reported = await internalBilling.ReportAsync(context, options.OrganizationId, RefreshAuthorityAsync, cancellationToken);
                    output.WriteLine(JsonSerializer.Serialize(reported.Bill, Json));
                    if (reported.Failure is not null)
                    {
                        failure = "Internal billing dependencies are unavailable.";
                    }
                    else if (!reported.Bill.Available)
                    {
                        throw new AccountAccessException(reported.Bill.Error ?? "Internal bill is unavailable.", 503);
                    }
                }
            }
            catch (Exception dependency) when (DependencyFailure(dependency, cancellationToken))
            { failure = "Internal billing dependencies are unavailable."; }
            catch (AccountAccessException rejected) { failure = rejected.Message; }
            catch (OnboardingValidationException rejected) { failure = rejected.Message; }
            catch (DeviceSignInException rejected) { failure = rejected.Message; }
            catch (SignInRejectedException) { failure = "CLI authorization was rejected."; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { failure = "Command canceled."; }
            catch (Exception) when (application is null) { failure = "Internal billing dependencies are unavailable."; }
            finally
            {
                // The device grant is never printed, cached or copied into a browser session.
                if (grant is not null && application is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await application.Services.GetRequiredService<TsAuthGateway>().DeviceRevokeAsync(grant, cleanup.Token); }
                    catch (Exception dependency) when (DependencyFailure(dependency, CancellationToken.None))
                    { failure ??= "Internal billing dependencies are unavailable."; }
                }
                if (application is not null)
                {
                    try { await application.DisposeAsync(); }
                    catch (Exception) { failure ??= "Internal billing dependencies are unavailable."; }
                }
            }
            if (failure is not null)
            {
                error.WriteLine(command + ": " + failure);
                return 1;
            }
            return 0;
        }

        private static ParsedArguments Parse(string[] args)
        {
            var command = args[0];
            if (command is not (Configure or Report))
            {
                return new CommandRejection("Unknown command.");
            }

            var names = command == Report ? new[] { "org", "admin-email" }
                : ["org", "admin-email", "base-plan", "spend-cap-cents", "write-cents-per-million", "storage-cents-per-gb-month"];
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument == "--")
                {
                    if (index != args.Length - 1)
                    {
                        return new CommandRejection("Unexpected positional arguments.");
                    }

                    break;
                }
                if (argument.Length < 2 || argument[0] != '-')
                {
                    return new CommandRejection("Unexpected positional arguments.");
                }

                var option = argument[(argument[1] == '-' ? 2 : 1)..];
                if (option.StartsWith('-'))
                {
                    return new CommandRejection("Invalid flag syntax: " + argument + ".");
                }

                var separator = option.IndexOf('=');
                var name = separator < 0 ? option : option[..separator];
                if (!names.Contains(name, StringComparer.Ordinal))
                {
                    return new CommandRejection("Unknown argument --" + name + ".");
                }

                if (separator < 0 && ++index >= args.Length)
                {
                    return new CommandRejection("--" + name + " requires a value.");
                }

                fields[name] = separator < 0 ? args[index] : option[(separator + 1)..];
            }
            foreach (var name in names)
            {
                if (!fields.ContainsKey(name))
                {
                    return new CommandRejection("--" + name + " is required.");
                }
            }

            var org = fields["org"];
            var email = fields["admin-email"].Trim();
            var plan = fields.GetValueOrDefault("base-plan", "");
            if (org.Trim().Length == 0 || email.Length == 0 || command == Configure && plan.Trim().Length == 0)
            {
                return new CommandRejection("--org, --admin-email and --base-plan must not be empty.");
            }

            if (org.Trim() != org || plan.Trim() != plan)
            {
                return new CommandRejection("--org and --base-plan must be exact IDs and keys without surrounding whitespace.");
            }

            if (command == Report)
            {
                return new Arguments(command, org, email, "", 0, 0, 0);
            }

            var fieldsToParse = new[] { "spend-cap-cents", "write-cents-per-million", "storage-cents-per-gb-month" };
            var values = new long[fieldsToParse.Length];
            for (var index = 0; index < fieldsToParse.Length; index++)
            {
                if (Integer(fields[fieldsToParse[index]], fieldsToParse[index], out values[index]) is { } invalid)
                {
                    return invalid;
                }
            }

            var cap = values[0];
            var writes = values[1];
            var storage = values[2];
            if (cap <= 0 || writes < 0 || storage < 0)
            {
                return new CommandRejection("--spend-cap-cents must be positive and estimate rates non-negative.");
            }

            return new Arguments(command, org, email, plan, cap, writes, storage);
        }

        // Go's Int64 flag parser accepts decimal and explicit binary/octal/hex
        // integers, including legal separators, while rejecting signed overflow.
        private static CommandRejection? Integer(string value, string name, out long parsed)
        {
            parsed = 0;
            var digits = value.AsSpan();
            var negative = false;
            if (!digits.IsEmpty && digits[0] is '-' or '+') { negative = digits[0] == '-'; digits = digits[1..]; }
            var radix = 10;
            var prefixed = false;
            if (digits.Length >= 2 && digits[0] == '0')
            {
                var prefix = char.ToLowerInvariant(digits[1]);
                if (prefix is 'x' or 'b' or 'o') { radix = prefix == 'x' ? 16 : prefix == 'b' ? 2 : 8; digits = digits[2..]; prefixed = true; }
                else { radix = 8; }
            }
            BigInteger total = 0;
            var previousDigit = false;
            var count = 0;
            foreach (var character in digits)
            {
                if (character == '_')
                {
                    if (!previousDigit && !(prefixed && count == 0))
                    {
                        return Invalid();
                    }

                    previousDigit = false; prefixed = false; continue;
                }
                var number = character is >= '0' and <= '9' ? character - '0' : character is >= 'a' and <= 'f' ? character - 'a' + 10 : character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                if (number < 0 || number >= radix)
                {
                    return Invalid();
                }

                total = total * radix + number;
                if (total > (BigInteger)long.MaxValue + (negative ? 1 : 0))
                {
                    return Invalid();
                }

                count++; previousDigit = true; prefixed = false;
            }
            if (count == 0 || !previousDigit)
            {
                return Invalid();
            }

            parsed = (long)(negative ? -total : total);
            return null;
            CommandRejection Invalid()
            {
                return new("--" + name + " requires a signed 64-bit integer.");
            }
        }

        private static bool DependencyFailure(Exception error, CancellationToken cancellationToken)
        {
            return error is TerraBaseException or HttpRequestException or IOException or JsonException or TimeoutException or InvalidOperationException or KeyNotFoundException or OverflowException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }
    }
}
