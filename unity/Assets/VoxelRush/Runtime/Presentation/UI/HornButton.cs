using UnityEngine;
using UnityEngine.InputSystem.OnScreen;
using UnityEngine.UI;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Presentation.Input;

namespace VoxelRush.Presentation.UI
{
    /// <summary>
    /// HORN touch button next to BUILD (COSM-002), shown only while the own car has a server-confirmed horn. Presses
    /// (touch, H key or left shoulder) go to the own car, which sends them to the server.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HornButton : MonoBehaviour
    {
        public const string ButtonName = "Horn";
        public const string TemplateName = "BuildMode";

        private GameObject? _button;
        private DrivingInputReader? _reader;

        public GameObject? Button => _button;

        private void Start()
        {
            _reader = FindAnyObjectByType<DrivingInputReader>();
            RectTransform? template = null;
            foreach (RectTransform rect in GetComponentsInChildren<RectTransform>(true))
            {
                if (rect.name == TemplateName)
                {
                    template = rect;
                    break;
                }
            }

            if (template == null)
            {
                return;
            }

            // Cloned inactive, so it never binds to the template's control path.
            bool wasActive = template.gameObject.activeSelf;
            template.gameObject.SetActive(false);
            _button = Instantiate(template.gameObject, template.parent, false);
            template.gameObject.SetActive(wasActive);
            _button.name = ButtonName;
            var placed = (RectTransform)_button.transform;
            placed.anchoredPosition = template.anchoredPosition + new Vector2(template.sizeDelta.x + 20f, 0f);
            OnScreenButton? onScreen = _button.GetComponent<OnScreenButton>();
            if (onScreen != null)
            {
                onScreen.controlPath = DrivingControls.TouchHornPath;
            }

            Text? label = _button.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = "HORN";
            }
        }

        private void Update()
        {
            if (_button != null)
            {
                bool show = LocalPlayer.Vehicle != null && CarCosmetics.Get(LocalPlayer.Vehicle.transform, CosmeticKind.Horn).Length > 0;
                if (_button.activeSelf != show)
                {
                    _button.SetActive(show);
                }
            }

            if (_reader != null && _reader.HornPressedThisFrame)
            {
                LocalPlayer.Honk();
            }
        }
    }
}
