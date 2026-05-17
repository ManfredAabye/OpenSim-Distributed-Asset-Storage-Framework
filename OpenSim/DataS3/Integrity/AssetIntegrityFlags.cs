namespace OpenSim.DataS3.Integrity
{
    /// <summary>
    /// Internal metadata flags used by integrity scans and repair routines.
    /// </summary>
    public static class AssetIntegrityFlags
    {
        /// <summary>
        /// Marks metadata rows that currently point to unreadable or inconsistent object data.
        /// </summary>
        public const int InconsistentEntry = 0x40000000;
    }
}