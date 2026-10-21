using UnityEngine;
using VoxelRush.Core.Input;

namespace VoxelRush.Presentation.UI
{
    /// <summary>Applies the player's button-size setting to the touch control groups.</summary>
    [DisallowMultipleComponent]
    public sealed class TouchControlsScaler : MonoBehaviour
    {
        [SerializeField] private RectTransform[] _groups = new RectTransform[0];

        public void Apply(ControlSettings settings)
        {
            foreach (RectTransform group in _groups)
            {
                if (group != null)
                {
                    group.localScale = new Vector3(settings.ButtonScale, settings.ButtonScale, 1f);
                }
            }
        }

        private void Start() => Apply(ControlSettings.Default);
    }
}
