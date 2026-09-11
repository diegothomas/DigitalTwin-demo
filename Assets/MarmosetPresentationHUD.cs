using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[DefaultExecutionOrder(-50)]
public sealed class MarmosetPresentationHUD : MonoBehaviour
{
    private sealed class StopMarker
    {
        public int nodeIndex;
        public Transform transform;
        public Renderer renderer;
        public Vector3 baseScale;
    }

    private const int OverviewWidth = 640;
    private const int OverviewHeight = 360;

    private readonly List<StopMarker> stopMarkers = new List<StopMarker>();
    private MaterialPropertyBlock markerProperties;

    private MarmosetNodeAnimator animator;
    private Canvas canvas;
    private Slider progressSlider;
    private Text statusText;
    private RawImage overviewImage;
    private Camera overviewCamera;
    private RenderTexture overviewTexture;
    private Material markerMaterial;
    private Font uiFont;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallForActiveMarmoset()
    {
        MarmosetNodeAnimator nodeAnimator = FindAnyObjectByType<MarmosetNodeAnimator>();
        if (nodeAnimator != null && nodeAnimator.GetComponent<MarmosetPresentationHUD>() == null)
            nodeAnimator.gameObject.AddComponent<MarmosetPresentationHUD>();
    }

    private void Start()
    {
        animator = GetComponent<MarmosetNodeAnimator>();
        if (animator == null || !animator.Initialize())
        {
            enabled = false;
            return;
        }

        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        markerProperties = new MaterialPropertyBlock();
        BuildInterface();
        BuildStopMarkers();
        BuildOverviewCamera();
        RefreshInterface();
    }

    private void Update()
    {
        if (animator == null)
            return;

        RefreshInterface();
        RefreshStopMarkers();
    }

    private void BuildInterface()
    {
        GameObject canvasObject = CreateUIObject("Marmoset Presentation HUD");
        canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasObject.AddComponent<GraphicRaycaster>();

        Image bottomPanel = CreateImage("Playback Panel", canvas.transform,
            new Color(0.025f, 0.035f, 0.05f, 0.9f));
        RectTransform panelRect = bottomPanel.rectTransform;
        panelRect.anchorMin = new Vector2(0f, 0f);
        panelRect.anchorMax = new Vector2(1f, 0f);
        panelRect.pivot = new Vector2(0.5f, 0f);
        panelRect.offsetMin = new Vector2(24f, 20f);
        panelRect.offsetMax = new Vector2(-24f, 94f);

        statusText = CreateText("Playback Status", bottomPanel.transform, 18, TextAnchor.MiddleLeft);
        RectTransform textRect = statusText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0.48f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.offsetMin = new Vector2(18f, 0f);
        textRect.offsetMax = new Vector2(-18f, -2f);

        GameObject sliderObject = CreateUIObject("Playback Progress", bottomPanel.transform);
        RectTransform sliderRect = sliderObject.GetComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0f, 0f);
        sliderRect.anchorMax = new Vector2(1f, 0.48f);
        sliderRect.offsetMin = new Vector2(18f, 14f);
        sliderRect.offsetMax = new Vector2(-18f, -9f);

        progressSlider = sliderObject.AddComponent<Slider>();
        progressSlider.minValue = 0f;
        progressSlider.maxValue = 1f;
        progressSlider.wholeNumbers = false;
        progressSlider.interactable = false;
        progressSlider.direction = Slider.Direction.LeftToRight;
        progressSlider.navigation = new Navigation { mode = Navigation.Mode.None };

        Image track = CreateImage("Track", sliderObject.transform, new Color(0.12f, 0.15f, 0.19f, 1f));
        Stretch(track.rectTransform, Vector2.zero, Vector2.zero);

        RectTransform fillArea = CreateRect("Fill Area", sliderObject.transform);
        Stretch(fillArea, Vector2.zero, Vector2.zero);
        Image fill = CreateImage("Fill", fillArea, new Color(0.13f, 0.78f, 0.96f, 1f));
        Stretch(fill.rectTransform, Vector2.zero, Vector2.zero);
        progressSlider.fillRect = fill.rectTransform;

        RectTransform handleArea = CreateRect("Handle Slide Area", sliderObject.transform);
        Stretch(handleArea, Vector2.zero, Vector2.zero);
        Image handle = CreateImage("Handle", handleArea, new Color(0.95f, 0.98f, 1f, 1f));
        RectTransform handleRect = handle.rectTransform;
        handleRect.anchorMin = new Vector2(0f, 0.5f);
        handleRect.anchorMax = new Vector2(0f, 0.5f);
        handleRect.sizeDelta = new Vector2(12f, 20f);
        progressSlider.handleRect = handleRect;
        progressSlider.targetGraphic = handle;

        for (int i = 0; i < animator.NodeCount; i++)
        {
            float ratio = animator.FrameCount > 1
                ? animator.GetNodeFrame(i) / (animator.FrameCount - 1f)
                : 0f;
            Image tick = CreateImage($"Node Tick {i}", sliderObject.transform,
                i == 0 || i == animator.NodeCount - 1
                    ? new Color(1f, 1f, 1f, 0.9f)
                    : new Color(1f, 0.64f, 0.12f, 0.95f));
            RectTransform tickRect = tick.rectTransform;
            tickRect.anchorMin = new Vector2(ratio, 0.5f);
            tickRect.anchorMax = new Vector2(ratio, 0.5f);
            tickRect.sizeDelta = new Vector2(3f, 24f);
        }

        Image overviewFrame = CreateImage("Cage Overview Frame", canvas.transform,
            new Color(0.025f, 0.035f, 0.05f, 0.94f));
        RectTransform overviewRect = overviewFrame.rectTransform;
        overviewRect.anchorMin = new Vector2(0f, 1f);
        overviewRect.anchorMax = new Vector2(0f, 1f);
        overviewRect.pivot = new Vector2(0f, 1f);
        overviewRect.anchoredPosition = new Vector2(24f, -24f);
        overviewRect.sizeDelta = new Vector2(390f, 244f);

        Text overviewLabel = CreateText("Overview Label", overviewFrame.transform, 15, TextAnchor.MiddleLeft);
        overviewLabel.text = "CAGE OVERVIEW";
        overviewLabel.color = new Color(0.72f, 0.86f, 0.94f, 1f);
        RectTransform labelRect = overviewLabel.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 1f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.pivot = new Vector2(0.5f, 1f);
        labelRect.offsetMin = new Vector2(10f, -29f);
        labelRect.offsetMax = new Vector2(-10f, -5f);

        GameObject imageObject = CreateUIObject("Cage Overview Image", overviewFrame.transform);
        RectTransform imageRect = imageObject.GetComponent<RectTransform>();
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = new Vector2(6f, 6f);
        imageRect.offsetMax = new Vector2(-6f, -31f);
        overviewImage = imageObject.AddComponent<RawImage>();
        overviewImage.color = Color.white;
        overviewImage.raycastTarget = false;
    }

    private void BuildStopMarkers()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        markerMaterial = new Material(shader)
        {
            name = "Marmoset Stop Marker Material",
            hideFlags = HideFlags.DontSave
        };

        for (int nodeIndex = 1; nodeIndex < animator.NodeCount; nodeIndex++)
        {
            GameObject markerObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            markerObject.name = $"Stop Marker {nodeIndex} - {animator.GetNodeName(nodeIndex)}";
            markerObject.hideFlags = HideFlags.DontSave;
            markerObject.transform.position = animator.GetNodeWorldPosition(nodeIndex) + Vector3.up * 0.18f;
            markerObject.transform.localScale = Vector3.one * 0.12f;

            Collider markerCollider = markerObject.GetComponent<Collider>();
            if (markerCollider != null)
                Destroy(markerCollider);

            Renderer markerRenderer = markerObject.GetComponent<Renderer>();
            markerRenderer.sharedMaterial = markerMaterial;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;

            stopMarkers.Add(new StopMarker
            {
                nodeIndex = nodeIndex,
                transform = markerObject.transform,
                renderer = markerRenderer,
                baseScale = markerObject.transform.localScale
            });
        }
    }

    private void BuildOverviewCamera()
    {
        GameObject cameraObject = CreateRuntimeObject("Cage Overview Camera");
        overviewCamera = cameraObject.AddComponent<Camera>();
        overviewCamera.clearFlags = CameraClearFlags.Skybox;
        overviewCamera.fieldOfView = 58f;
        overviewCamera.nearClipPlane = 0.03f;
        overviewCamera.farClipPlane = 100f;
        overviewCamera.depth = -20f;
        overviewCamera.allowHDR = true;
        overviewCamera.allowMSAA = true;

        UniversalAdditionalCameraData cameraData = overviewCamera.GetUniversalAdditionalCameraData();
        cameraData.renderType = CameraRenderType.Base;
        cameraData.renderPostProcessing = false;

        overviewTexture = new RenderTexture(OverviewWidth, OverviewHeight, 24, RenderTextureFormat.ARGB32)
        {
            name = "Marmoset Cage Overview",
            antiAliasing = 2,
            useMipMap = false,
            autoGenerateMips = false
        };
        overviewTexture.Create();
        overviewCamera.targetTexture = overviewTexture;
        overviewImage.texture = overviewTexture;

        Vector3 target = GetPathCenter() + Vector3.up * 0.35f;
        if (TryGetCageBounds(out Bounds cageBounds))
        {
            float insetX = Mathf.Max(0.08f, cageBounds.size.x * 0.1f);
            float insetZ = Mathf.Max(0.08f, cageBounds.size.z * 0.1f);
            Vector3 cameraPosition = new Vector3(
                cageBounds.min.x + insetX,
                cageBounds.min.y + cageBounds.size.y * 0.78f,
                cageBounds.min.z + insetZ);

            if ((cameraPosition - target).sqrMagnitude < 0.64f)
                cameraPosition = target + new Vector3(-1.8f, 1.25f, -1.8f);

            overviewCamera.transform.position = cameraPosition;
        }
        else
        {
            overviewCamera.transform.position = target + new Vector3(-2.2f, 1.6f, -2.2f);
        }

        overviewCamera.transform.rotation = Quaternion.LookRotation(
            target - overviewCamera.transform.position,
            Vector3.up);
        
        overviewCamera.transform.position = new Vector3(-4.65f, 1, -4.04f);
        overviewCamera.transform.rotation = Quaternion.Euler(24.847f, 43.656f, -3.451f);
    }

    private void RefreshInterface()
    {
        if (progressSlider == null || statusText == null)
            return;

        progressSlider.SetValueWithoutNotify(animator.NormalizedProgress);
        string state = animator.IsPlaying ? "PLAYING" : "PAUSED";
        string segment = animator.CurrentNodeIndex < animator.NodeCount - 1
            ? $"{animator.CurrentNodeName}  >  {animator.NextNodeName}"
            : animator.CurrentNodeName;
        statusText.text = $"{state}   |   Frame {animator.CurrentFrame:000} / {Mathf.Max(0, animator.FrameCount - 1):000}" +
                          $"   |   {segment}   |   SPACE  Next segment    R  Reset";
    }

    private void RefreshStopMarkers()
    {
        markerProperties ??= new MaterialPropertyBlock();
        int nextNodeIndex = animator.CurrentNodeIndex + 1;
        float pulse = 1f + (Mathf.Sin(Time.unscaledTime * 5f) * 0.5f + 0.5f) * 0.32f;

        foreach (StopMarker marker in stopMarkers)
        {
            if (marker.transform == null || marker.renderer == null)
                continue;

            marker.transform.position = animator.GetNodeWorldPosition(marker.nodeIndex) + Vector3.up * 0.18f;
            bool isNext = marker.nodeIndex == nextNodeIndex && animator.CurrentNodeIndex < animator.NodeCount - 1;
            bool isReached = marker.nodeIndex <= animator.CurrentNodeIndex;
            marker.transform.localScale = marker.baseScale * (isNext ? pulse : 1f);

            Color color = isNext
                ? new Color(1f, 0.74f, 0.05f, 1f)
                : isReached
                    ? new Color(0.25f, 0.28f, 0.31f, 1f)
                    : new Color(1f, 0.05f, 0.035f, 1f);
            marker.renderer.GetPropertyBlock(markerProperties);
            markerProperties.SetColor("_BaseColor", color);
            markerProperties.SetColor("_Color", color);
            marker.renderer.SetPropertyBlock(markerProperties);
        }
    }

    private Vector3 GetPathCenter()
    {
        if (animator.NodeCount == 0)
            return transform.position;

        Vector3 sum = Vector3.zero;
        for (int i = 0; i < animator.NodeCount; i++)
            sum += animator.GetNodeWorldPosition(i);
        return sum / animator.NodeCount;
    }

    private static bool TryGetCageBounds(out Bounds bounds)
    {
        GameObject cage = GameObject.Find("20240719_Cage_Room_With_Marmoset_V1");
        if (cage == null)
        {
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.activeInHierarchy && root.name.Contains("Cage_Room") &&
                    !root.name.EndsWith("_marmo"))
                {
                    cage = root;
                    break;
                }
            }
        }

        bounds = default;
        if (cage == null)
            return false;

        bool found = false;
        foreach (Renderer renderer in cage.GetComponentsInChildren<Renderer>(false))
        {
            if (renderer is SkinnedMeshRenderer)
                continue;
            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    private Text CreateText(string name, Transform parent, int fontSize, TextAnchor alignment)
    {
        GameObject textObject = CreateUIObject(name, parent);
        Text text = textObject.AddComponent<Text>();
        text.font = uiFont;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = new Color(0.91f, 0.95f, 0.98f, 1f);
        text.raycastTarget = false;
        return text;
    }

    private static Image CreateImage(string name, Transform parent, Color color)
    {
        GameObject imageObject = CreateUIObject(name, parent);
        Image image = imageObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        GameObject rectObject = CreateUIObject(name, parent);
        return rectObject.GetComponent<RectTransform>();
    }

    private static GameObject CreateRuntimeObject(string name, Transform parent = null)
    {
        GameObject runtimeObject = new GameObject(name)
        {
            hideFlags = HideFlags.DontSave
        };
        if (parent != null)
            runtimeObject.transform.SetParent(parent, false);
        return runtimeObject;
    }

    private static GameObject CreateUIObject(string name, Transform parent = null)
    {
        GameObject uiObject = new GameObject(name, typeof(RectTransform))
        {
            hideFlags = HideFlags.DontSave
        };
        if (parent != null)
            uiObject.transform.SetParent(parent, false);
        return uiObject;
    }

    private static void Stretch(RectTransform rectTransform, Vector2 minOffset, Vector2 maxOffset)
    {
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = minOffset;
        rectTransform.offsetMax = maxOffset;
    }

    private void OnDestroy()
    {
        foreach (StopMarker marker in stopMarkers)
        {
            if (marker.transform != null)
                Destroy(marker.transform.gameObject);
        }

        if (overviewCamera != null)
            Destroy(overviewCamera.gameObject);
        if (canvas != null)
            Destroy(canvas.gameObject);
        if (overviewTexture != null)
        {
            overviewTexture.Release();
            Destroy(overviewTexture);
        }
        if (markerMaterial != null)
            Destroy(markerMaterial);
    }
}
