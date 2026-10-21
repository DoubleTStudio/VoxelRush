using System.Collections;
using UnityEngine.SceneManagement;

namespace VoxelRush.Tests.PlayMode
{
    internal static class TestScenes
    {
        /// <summary>
        /// Replaces all loaded scenes with an empty one. The HUD's on-screen controls own a virtual gamepad, so they
        /// must be torn down before InputTestFixture swaps the input system state (or after a non-fixture test).
        /// </summary>
        public static IEnumerator UnloadAll()
        {
            Scene empty = SceneManager.CreateScene("Empty_" + System.Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(empty);
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene != empty && scene.isLoaded)
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
            }
        }
    }
}
