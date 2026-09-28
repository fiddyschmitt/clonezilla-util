using System;

namespace libClonezilla
{
    /// <summary>
    /// A failure that ends the run: Program.Main logs <see cref="Exception.Message"/> (no stack trace) and exits with
    /// the code for <see cref="Reason"/>. Library code throws this instead of calling Environment.Exit, which reused
    /// code 1 for unrelated failures and bypassed the exit-code logging (gh review F56).
    /// </summary>
    public class FatalException(FatalReason reason, string message) : Exception(message)
    {
        public FatalReason Reason { get; } = reason;
    }

    public enum FatalReason
    {
        InvalidArguments,
        DokanNotInstalled,
        MountFailed,
    }
}
