using System.Diagnostics;
using System.Globalization;

namespace TerraScale.Dashboard.Features.Backups
{
    public abstract record BackupScheduleChoice;
    public sealed record EveryHours(int Hours) : BackupScheduleChoice;
    public sealed record Daily(int Hour) : BackupScheduleChoice;
    public sealed record Weekly(int Hour, int Day) : BackupScheduleChoice;
    public sealed record CustomSchedule(string Cron) : BackupScheduleChoice;

    public static class BackupSchedule
    {
        public static readonly string[] Weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

        public static BackupScheduleChoice Parse(string cron)
        {
            var parts = cron.Split(' ');
            if (parts.Length == 5 && parts[0] == "0" && parts[2] == "*" && parts[3] == "*" && parts[4] == "*")
            {
                if (parts[1] == "*/6")
                {
                    return new EveryHours(6);
                }

                if (parts[1] == "*/12")
                {
                    return new EveryHours(12);
                }

                if (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var hour) && hour is >= 0 and < 24)
                {
                    return new Daily(hour);
                }
            }

            if (parts.Length == 5 && parts[0] == "0" && parts[2] == "*" && parts[3] == "*" &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var weeklyHour) && weeklyHour is >= 0 and < 24 &&
                int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 0 and <= 6)
            {
                return new Weekly(weeklyHour, day);
            }

            return new CustomSchedule(cron);
        }

        public static string Cron(BackupScheduleChoice choice)
        {
            return choice switch
            {
                EveryHours(var hours) => $"0 */{hours.ToString(CultureInfo.InvariantCulture)} * * *",
                Daily(var hour) => $"0 {hour.ToString(CultureInfo.InvariantCulture)} * * *",
                Weekly(var hour, var day) => $"0 {hour.ToString(CultureInfo.InvariantCulture)} * * {day.ToString(CultureInfo.InvariantCulture)}",
                CustomSchedule(var cron) => cron,
                _ => throw new UnreachableException(),
            };
        }

        public static string HourLabel(int hour)
        {
            return hour.ToString("00", CultureInfo.InvariantCulture) + ":00";
        }

        public static string Words(string cron)
        {
            return cron.Length == 0 ? "No schedule" : Words(Parse(cron), cron);
        }

        public static string Words(BackupScheduleChoice choice, string cron)
        {
            return choice switch
            {
                EveryHours(var hours) => $"Every {hours.ToString(CultureInfo.InvariantCulture)} hours",
                Daily(var hour) => $"Daily at {HourLabel(hour)} UTC",
                Weekly(var hour, var day) => $"Weekly on {Weekdays[day]} at {HourLabel(hour)} UTC",
                CustomSchedule => cron,
                _ => throw new UnreachableException(),
            };
        }
    }
}
