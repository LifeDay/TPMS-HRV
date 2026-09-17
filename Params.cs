namespace TpmsHrv;

public static class Params
{
    public const float fVoxelSizeMM     = 0.5f;   // dev; 0.15f for production
    public const float fCellSizeMM      = 8.0f;   // gyroid lambda
    public const float fWallThicknessMM = 0.8f;
    public const float fPortSealDepthMM = 6.0f;   // ~0.75 * lambda
    public const float fSkinThicknessMM = 1.5f;

    // RESOLVED (Task 2): the fixture OBJs carry "# ... Units = meters" in their
    // header comment, and the cube's vertices span +/-0.05 about the origin
    // (0.1 m == 100 mm edge length). So OBJ units are meters; PicoGK is mm.
    public const float fImportScale = 1000.0f;

    public const float fColorTolerance = 0.15f;   // RGB distance
}
