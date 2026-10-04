using System;

namespace GameRandomiser.Core.Diagnostics
{
    public enum ErrorCategory
    {
        General,
        Persistence,
        Configuration,
        WheelRefresh,
        PopulationRule,
        GameResolution,
        Artwork,
        Selection,
        Initialisation,
        Environment
    }

    /// <summary>
    /// One failed operation: enough for a useful log line and a user message that says what failed,
    /// whether data is safe and what to do next. Stack traces stay in the log.
    /// </summary>
    public sealed class RandomiserError
    {
        public RandomiserError(ErrorCategory category, string operation, string userMessage, Exception exception = null,
            Guid? wheelId = null, string recovery = null, DateTime? timestampUtc = null)
        {
            Category = category;
            Operation = operation ?? string.Empty;
            UserMessage = userMessage ?? string.Empty;
            Exception = exception;
            WheelId = wheelId;
            Recovery = recovery;
            TimestampUtc = timestampUtc ?? DateTime.UtcNow;
        }

        public ErrorCategory Category { get; }

        /// <summary>Short verb phrase, e.g. "refresh wheel".</summary>
        public string Operation { get; }

        public string UserMessage { get; }
        public Exception Exception { get; }
        public Guid? WheelId { get; }

        /// <summary>What the extension did to stay usable, e.g. "kept previous game list".</summary>
        public string Recovery { get; }

        public DateTime TimestampUtc { get; }

        /// <summary>Single structured line for the log. Contains ids, never game names or file contents.</summary>
        public string ToLogString() =>
            $"[{Category}] operation=\"{Operation}\""
            + (WheelId.HasValue ? $" wheel={WheelId.Value}" : string.Empty)
            + (Exception != null ? $" exception={Exception.GetType().Name}" : string.Empty)
            + (string.IsNullOrEmpty(Recovery) ? string.Empty : $" recovery=\"{Recovery}\"")
            + $" at={TimestampUtc:o}";
    }

    public interface IErrorReporter
    {
        void Report(RandomiserError error);
    }

    /// <summary>A wheel's criteria cannot be evaluated (unknown rule or invalid parameter).</summary>
    public sealed class InvalidPopulationRuleException : Exception
    {
        public InvalidPopulationRuleException(string message) : base(message)
        {
        }
    }

    /// <summary>The library could not be read, so a refresh must not be trusted.</summary>
    public sealed class LibraryUnavailableException : Exception
    {
        public LibraryUnavailableException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>Canonical user-facing wording, so equivalent failures read the same everywhere.</summary>
    public static class UserMessages
    {
        public const string RefreshFailed =
            "Unable to refresh this wheel. Your previous game list has been retained. Try refreshing again or review the diagnostic information.";

        public const string InvalidPopulationRule =
            "This wheel contains an invalid population rule. Review its criteria before refreshing.";

        public const string LibraryUnavailable =
            "Your Playnite library could not be read, so this wheel was not refreshed. Your previous game list has been retained.";

        public const string SaveFailed =
            "Your changes could not be saved. The previous saved configuration has been preserved, and Game Randomiser will try again on your next change.";

        public const string NoCriteria =
            "This wheel wasn't created from criteria, so it can't be kept in sync automatically.";

        public const string PinRequiresPolicy =
            "Pinning is only available on wheels that use \"Criteria + pinned games\".";

        public const string AllCandidatesExcluded =
            "Every game on this wheel was a recent winner, so there is nothing left to pick. Reset reroll protection to spin again.";

        public const string RerollLimitReached =
            "You've used all your rerolls. Accept the current pick, or reset reroll protection to spin again.";

        public const string SingleGameBypass =
            "This wheel has only one game, so recent-winner protection doesn't apply.";

        public static string CoolingDown(TimeSpan remaining)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return $"You've used all your rerolls. Rerolls are available again in {minutes} {(minutes == 1 ? "minute" : "minutes")}, or accept the current pick.";
        }

        /// <summary>Maps a refresh failure to its category and user message.</summary>
        public static string ForRefreshFailure(Exception exception, out ErrorCategory category)
        {
            if (exception is InvalidPopulationRuleException)
            {
                category = ErrorCategory.PopulationRule;
                return InvalidPopulationRule;
            }

            if (exception is LibraryUnavailableException)
            {
                category = ErrorCategory.GameResolution;
                return LibraryUnavailable;
            }

            category = ErrorCategory.WheelRefresh;
            return RefreshFailed;
        }
    }
}
