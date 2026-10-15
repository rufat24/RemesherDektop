using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// The areas painted on a source mesh for more or fewer quads (the Native Remesher window's
    /// Paint Fix Areas), kept on the source object so a remesh run again reads them. Editor data:
    /// it does nothing at runtime.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class RemeshPaint : MonoBehaviour
    {
        public DensityPaint paint = new DensityPaint();
    }
}
