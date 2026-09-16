using System.Globalization;
using System.Numerics;

namespace TpmsHrv;

public struct Tri
{
    public Vector3 vecA, vecB, vecC;
}

public class ObjGroup
{
    public string strName = "";
    public Vector3 vecKd;
    public List<Tri> oTris = new();
}

public static class ObjImporter
{
    static readonly char[] awsSeparators = { ' ', '\t' };

    /// <summary>
    /// Parses an OBJ + its referenced MTL. Groups triangles by the active
    /// `usemtl` material (not by `o`/`g`), since Onshape emits a fresh `o`
    /// block per sub-mesh even when the material hasn't changed - a filleted
    /// port face becomes many `o` blocks sharing one material.
    /// </summary>
    public static List<ObjGroup> oLoad(string strObjPath)
    {
        string strDir = Path.GetDirectoryName(Path.GetFullPath(strObjPath)) ?? ".";

        var avecVertex = new List<Vector3>();
        var oMtlKd = new Dictionary<string, Vector3>();
        var oGroupsByMaterial = new Dictionary<string, ObjGroup>();
        var oGroupsInOrder = new List<ObjGroup>();
        ObjGroup? oActive = null;

        foreach (string strRawLine in File.ReadLines(strObjPath))
        {
            string strLine = strRawLine.Trim();
            if (strLine.Length == 0 || strLine[0] == '#')
                continue;

            string[] aToken = strLine.Split(awsSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (aToken.Length == 0)
                continue;

            switch (aToken[0])
            {
                case "mtllib":
                    oMtlKd = oLoadMtl(Path.Combine(strDir, aToken[1]));
                    break;

                case "v":
                    avecVertex.Add(new Vector3(
                        float.Parse(aToken[1], CultureInfo.InvariantCulture),
                        float.Parse(aToken[2], CultureInfo.InvariantCulture),
                        float.Parse(aToken[3], CultureInfo.InvariantCulture)));
                    break;

                case "usemtl":
                {
                    string strMtl = aToken[1];
                    if (!oGroupsByMaterial.TryGetValue(strMtl, out oActive))
                    {
                        Vector3 vecKd = oMtlKd.TryGetValue(strMtl, out Vector3 vec) ? vec : Vector3.Zero;
                        oActive = new ObjGroup { strName = strMtl, vecKd = vecKd };
                        oGroupsByMaterial[strMtl] = oActive;
                        oGroupsInOrder.Add(oActive);
                    }
                    break;
                }

                case "f":
                {
                    // A face before any usemtl is a real (if unlikely) case -
                    // fall back to an implicit unpainted group rather than crash.
                    if (oActive is null)
                    {
                        oActive = new ObjGroup { strName = "(none)", vecKd = Vector3.Zero };
                        oGroupsByMaterial[""] = oActive;
                        oGroupsInOrder.Add(oActive);
                    }

                    int nCorners = aToken.Length - 1;
                    var anIdx = new int[nCorners];
                    for (int i = 0; i < nCorners; i++)
                        anIdx[i] = nResolveVertexIndex(aToken[i + 1], avecVertex.Count);

                    // Fan-triangulate quads/n-gons.
                    for (int i = 1; i + 1 < nCorners; i++)
                    {
                        oActive.oTris.Add(new Tri
                        {
                            vecA = avecVertex[anIdx[0]],
                            vecB = avecVertex[anIdx[i]],
                            vecC = avecVertex[anIdx[i + 1]],
                        });
                    }
                    break;
                }

                // 'o' / 'g' intentionally ignored - see method summary.
            }
        }

        return oGroupsInOrder;
    }

    /// <summary>OBJ face indices: 1-indexed, "v", "v/vt", "v//vn", or "v/vt/vn"; may be negative.</summary>
    static int nResolveVertexIndex(string strCorner, int nVertexCountSoFar)
    {
        string strVIdx = strCorner.Split('/')[0];
        int n = int.Parse(strVIdx, CultureInfo.InvariantCulture);
        return n > 0 ? n - 1 : nVertexCountSoFar + n;
    }

    static Dictionary<string, Vector3> oLoadMtl(string strMtlPath)
    {
        var oResult = new Dictionary<string, Vector3>();
        string strCurrentName = "";

        foreach (string strRawLine in File.ReadLines(strMtlPath))
        {
            string strLine = strRawLine.Trim();
            if (strLine.Length == 0 || strLine[0] == '#')
                continue;

            string[] aToken = strLine.Split(awsSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (aToken.Length == 0)
                continue;

            if (aToken[0] == "newmtl")
            {
                strCurrentName = aToken[1];
            }
            else if (aToken[0] == "Kd" && strCurrentName.Length > 0)
            {
                oResult[strCurrentName] = new Vector3(
                    float.Parse(aToken[1], CultureInfo.InvariantCulture),
                    float.Parse(aToken[2], CultureInfo.InvariantCulture),
                    float.Parse(aToken[3], CultureInfo.InvariantCulture));
            }
        }

        return oResult;
    }

    /// <summary>Area-weighted centroid, robust to uneven tessellation (e.g. a fillet band).</summary>
    public static Vector3 vecCentroid(ObjGroup oGroup)
    {
        Vector3 vecWeightedSum = Vector3.Zero;
        float fAreaSum = 0f;

        foreach (Tri t in oGroup.oTris)
        {
            float fArea2 = Vector3.Cross(t.vecB - t.vecA, t.vecC - t.vecA).Length();
            Vector3 vecTriCentroid = (t.vecA + t.vecB + t.vecC) / 3f;
            vecWeightedSum += vecTriCentroid * fArea2;
            fAreaSum += fArea2;
        }

        return fAreaSum > 0f ? vecWeightedSum / fAreaSum : Vector3.Zero;
    }

    public static (Vector3 vecMin, Vector3 vecMax) oBoundingBox(IEnumerable<ObjGroup> oGroups)
    {
        Vector3 vecMin = new(float.MaxValue), vecMax = new(float.MinValue);
        foreach (ObjGroup oGroup in oGroups)
        {
            foreach (Tri t in oGroup.oTris)
            {
                foreach (Vector3 vec in new[] { t.vecA, t.vecB, t.vecC })
                {
                    vecMin = Vector3.Min(vecMin, vec);
                    vecMax = Vector3.Max(vecMax, vec);
                }
            }
        }
        return (vecMin, vecMax);
    }
}
