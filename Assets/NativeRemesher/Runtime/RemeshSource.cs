using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// On a remeshed result: the object it was made from, so the Native Remesher window can paint
    /// fixes on the result and run the source again. Editor data: it does nothing at runtime.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class RemeshSource : MonoBehaviour
    {
        public GameObject source;
    }
}
