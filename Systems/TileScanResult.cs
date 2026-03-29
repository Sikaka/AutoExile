using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Result of scanning tile terrain data for a map instance.
    /// Ephemeral — computed fresh each map entry, never persisted.
    /// Tile data covers the entire map grid at zone load, giving instant
    /// mechanic detection and position data before any exploration.
    /// </summary>
    public class TileScanResult
    {
        public string AreaName = "";
        public bool IsScanned;

        /// <summary>
        /// Mechanics detected from tile path prefixes (map-wide, no blob filter).
        /// Key = mechanic name ("Ultimatum", "Harvest", etc.)
        /// Populated on initial scan at map load (before exploration).
        /// </summary>
        public Dictionary<string, MechanicTileInfo> DetectedMechanics = new();

        /// <summary>
        /// Blob-relative landmarks — tile clusters that are unusual within the active blob.
        /// Populated when ProcessTileScan runs with blob data (after exploration init).
        /// Includes mechanics, transitions, and unknown features.
        /// </summary>
        public List<BlobLandmark> Landmarks = new();
    }

    public class MechanicTileInfo
    {
        public string MechanicName = "";
        public string MatchedPathPrefix = "";
        public int TileCount;
        /// <summary>Centroid of all matched tile positions (grid coordinates, tile-center offset applied).</summary>
        public Vector2 CentroidGridPos;
    }

    public enum LandmarkType
    {
        Unknown,    // Unusual tile cluster, purpose not known
        Mechanic,   // Matches known mechanic detail name
        Transition, // Likely area transition (e.g., boss entrance)
    }

    public class BlobLandmark
    {
        public string DetailName = "";
        public Vector2 CentroidGridPos;
        public int TileCount;          // tiles in this cluster
        public int TotalInBlob;        // total tiles with this name in the blob
        public LandmarkType Type = LandmarkType.Unknown;
    }
}
