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
            Arguments options;
            try { options = Parse(args); }
            catch (CommandRejectedException rejected)
            {
                await error.WriteLineAsync(command + ": " + rejected.Message);
                return 1;
            }

            SessionRecord? grant = null;
            string? failure = null;
            WebApplication? application = null;
            try
            {
                application = buildApplication([]) ?? throw new InvalidOperationException("The command application is unavailable.");
                // Commands receive exactly the same automatic persistent-schema
                // initialization as the hosted application's admission boundary.
                await application.Services.GetRequiredService<IDashboardState>().InitializeAsync(cancellationToken);
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
                    await output.WriteLineAsync($"configured report-only internal billing for organization {options.OrganizationId} using base plan {options.BasePlanKey}");
                }
                else
                {
                    var bill = await internalBilling.ReportAsync(context, options.OrganizationId, RefreshAuthorityAsync, cancellationToken);
                    await output.WriteLineAsync(JsonSerializer.Serialize(bill, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    if (!bill.Available)
                    {
                        throw new AccountAccessException(bill.Error ?? "Internal bill is unavailable.", 503);
                    }
                }
            }
            catch (InternalBillingReportException report)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(report.Bill, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                failure = report.InnerException is AccountAccessException rejected ? rejected.Message : "Internal billing dependencies are unavailable.";
            }
            catch (Exception dependency) when (DependencyFailure(dependency, cancellationToken))
            { failure = "Internal billing dependencies are unavailable."; }
            catch (AccountAccessException rejected) { failure = rejected.Message; }
            catch (OnboardingValidationException rejected) { failure = rejected.Message; }
            catch (CommandRejectedException rejected) { failure = rejected.Message; }
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
                await error.WriteLineAsync(command + ": " + failure);
                return 1;
            }
            return 0;
        }

        private static Arguments Parse(string[] args)
        {
            var command = args[0];
            if (command is not (Configure or Report))
            {
                throw new CommandRejectedException("Unknown command.");
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
                        throw new CommandRejectedException("Unexpected positional arguments.");
                    }

                    break;
                }
                if (argument.Length < 2 || argument[0] != '-')
                {
                    throw new CommandRejectedException("Unexpected positional arguments.");
                }

                var option = argument[(argument[1] == '-' ? 2 : 1)..];
                if (option.StartsWith('-'))
                {
                    throw new CommandRejectedException("Invalid flag syntax: " + argument + ".");
                }

                var separator = option.IndexOf('=');
                var name = separator < 0 ? option : option[..separator];
                if (!names.Contains(name, StringComparer.Ordinal))
                {
                    throw new CommandRejectedException("Unknown argument --" + name + ".");
                }

                if (separator < 0 && ++index >= args.Length)
                {
                    throw new CommandRejectedException("--" + name + " requires a value.");
                }

                fields[name] = separator < 0 ? args[index] : option[(separator + 1)..];
            }
            foreach (var name in names)
            {
                if (!fields.ContainsKey(name))
                {
                    throw new CommandRejectedException("--" + name + " is required.");
                }
            }

            var org = fields["org"];
            var email = fields["admin-email"].Trim();
            var plan = fields.GetValueOrDefault("base-plan", "");
            if (org.Trim().Length == 0 || email.Length == 0 || command == Configure && plan.Trim().Length == 0)
            {
                throw new CommandRejectedException("--org, --admin-email and --base-plan must not be empty.");
            }

            if (org.Trim() != org || plan.Trim() != plan)
            {
                throw new CommandRejectedException("--org and --base-plan must be exact IDs and keys without surrounding whitespace.");
            }

            if (command == Report)
            {
                return new(command, org, email, "", 0, 0, 0);
            }

            var cap = Integer(fields["spend-cap-cents"], "spend-cap-cents");
            var writes = Integer(fields["write-cents-per-million"], "write-cents-per-million");
            var storage = Integer(fields["storage-cents-per-gb-month"], "storage-cents-per-gb-month");
            if (cap <= 0 || writes < 0 || storage < 0)
            {
                throw new CommandRejectedException("--spend-cap-cents must be positive and estimate rates non-negative.");
            }

            return new(command, org, email, plan, cap, writes, storage);
        }

        // Go's Int64 flag parser accepts decimal and explicit binary/octal/hex
        // integers, including legal separators, while rejecting signed overflow.
        private static long Integer(string value, string name)
        {
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
                        throw Invalid();
                    }

                    previousDigit = false; prefixed = false; continue;
                }
                var number = character is >= '0' and <= '9' ? character - '0' : character is >= 'a' and <= 'f' ? character - 'a' + 10 : character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                if (number < 0 || number >= radix)
                {
                    throw Invalid();
                }

                total = total * radix + number;
                if (total > (BigInteger)long.MaxValue + (negative ? 1 : 0))
                {
                    throw Invalid();
                }

                count++; previousDigit = true; prefixed = false;
            }
            if (count == 0 || !previousDigit)
            {
                throw Invalid();
            }

            return (long)(negative ? -total : total);
            CommandRejectedException Invalid()
            {
                return new("--" + name + " requires a signed 64-bit integer.");
            }
        }

        private static bool DependencyFailure(Exception error, CancellationToken cancellationToken)
        {
            return error is HttpRequestException or IOException or JsonException or TimeoutException or InvalidOperationException or KeyNotFoundException or OverflowException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }

        private sealed record Arguments(string Command, string OrganizationId, string AdminEmail, string BasePlanKey,
            long SpendCapCents, long WriteCentsPerMillion, long StorageCentsPerGBMonth);
        private sealed class CommandRejectedException(string message) : Exception(message);
    }
}
