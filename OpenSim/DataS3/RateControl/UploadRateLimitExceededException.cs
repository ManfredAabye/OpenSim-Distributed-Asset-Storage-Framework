using System;
using OpenSim.DataS3.Models;
using OpenSim.Framework;

namespace OpenSim.DataS3.RateControl
{
    /// <summary>
    /// Exception that signals an upload was denied by rate control and should map to HTTP 429 upstream.
    /// </summary>
    public sealed class UploadRateLimitExceededException : Exception
    {
        /// <summary>
        /// Creates a new rate-limit exception carrying the current quota status.
        /// </summary>
        /// <param name="message">Human-readable error message.</param>
        /// <param name="status">Quota status at decision time.</param>
        public UploadRateLimitExceededException(string message, QuotaStatus status)
            : base(message)
        {
            Status = status;
        }

        /// <summary>
        /// Quota status used to build client-facing Retry-After hints.
        /// </summary>
        public QuotaStatus Status { get; }
    }
}
