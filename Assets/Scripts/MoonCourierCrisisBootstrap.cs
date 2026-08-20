using UnityEngine;

public class MoonCourierCrisisBootstrap : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureGameExists()
    {
        if (Object.FindFirstObjectByType<MoonCourierCrisisGame>() != null) return;
        var go = new GameObject("Moon Courier Crisis Game");
        go.AddComponent<MoonCourierCrisisGame>();
    }
}
