using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

public class MoonCourierCrisisGame : MonoBehaviour
{
    [Serializable]
    public class RoverData
    {
        public string id;
        public string roverName;
        public float battery;
        public float capacity;
        public string status;
        public Vector3 position;
        public bool inspectionDone;
        public string damage;
        public int repairCost;
    }

    [Serializable]
    public class OrderData
    {
        public string id;
        public string title;
        public float weight;
        public int reward;
        public int urgency;
        public float risk;
        public string zone;
        public string status;
        public Vector3 position;
    }

    [Serializable]
    public class DeliveryData
    {
        public string id;
        public string roverId;
        public string orderId;
        public float batterySpent;
        public bool success;
        public int reward;
        public string eventText;
        public int day;
    }

    [Serializable]
    public class GameSave
    {
        public int credits;
        public int score;
        public int day;
        public int deliveriesToday;
        public int dayStartCredits;
        public int dayStartScore;
        public List<RoverData> rovers = new List<RoverData>();
        public List<OrderData> orders = new List<OrderData>();
        public List<DeliveryData> deliveries = new List<DeliveryData>();
        public List<string> events = new List<string>();
    }

    private GameSave data = new GameSave();
    private readonly Dictionary<string, GameObject> roverObjects = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, GameObject> orderObjects = new Dictionary<string, GameObject>();

    private RoverData selectedRover;
    private OrderData selectedOrder;
    private Camera cam;
    private LineRenderer routeLine;
    private bool deliveryAnimating;
    private Shader surfaceShader;
    private Shader litShader;
    private Shader unlitShader;
    private Texture2D lunarAlbedoTexture;
    private Texture2D lunarNormalTexture;
    private readonly List<Vector2> routeObstacleCenters = new List<Vector2>();
    private readonly List<float> routeObstacleRadii = new List<float>();

    private struct CraterSpec
    {
        public Vector2 center;
        public float radius;
        public float depth;
    }
    private readonly List<CraterSpec> craterSpecs = new List<CraterSpec>();

    private struct OutcropSpec
    {
        public Vector2 center;
        public float radius;
        public float height;
        public float skew;
    }
    private readonly List<OutcropSpec> outcropSpecs = new List<OutcropSpec>();

    private enum TerrainKind
    {
        Plain,
        Rough,
        Crater
    }

    bool InTerrainEllipse(float x, float z, float cx, float cz, float rx, float rz)
    {
        float nx = (x - cx) / Mathf.Max(.01f, rx);
        float nz = (z - cz) / Mathf.Max(.01f, rz);
        return nx * nx + nz * nz <= 1f;
    }

    float TerrainEllipseInfluence(float x, float z, float cx, float cz, float rx, float rz)
    {
        float nx = (x - cx) / Mathf.Max(.01f, rx);
        float nz = (z - cz) / Mathf.Max(.01f, rz);
        float d = Mathf.Sqrt(nx * nx + nz * nz);

        // Широкое мягкое смешивание убирает резкие вертикальные швы между зонами.
        return 1f - Mathf.SmoothStep(.68f, 1.16f, d);
    }

    void TerrainVisualWeightsAt(float x, float z, out float roughWeight, out float craterWeight)
    {
        craterWeight = Mathf.Max(
            TerrainEllipseInfluence(x,z,4f,15f,6.8f,5.7f),
            TerrainEllipseInfluence(x,z,15f,11f,7.8f,6.4f)
        );

        roughWeight = Mathf.Max(
            TerrainEllipseInfluence(x,z,-7f,14f,7.8f,6.6f),
            TerrainEllipseInfluence(x,z,12f,1f,7.2f,5.8f)
        );

        roughWeight *= (1f - craterWeight);
    }

    TerrainKind TerrainKindAt(float x, float z)
    {
        // Кратерные поля имеют приоритет над неровным грунтом в местах пересечения.
        if (InTerrainEllipse(x,z,4f,15f,6.8f,5.7f) ||
            InTerrainEllipse(x,z,15f,11f,7.8f,6.4f))
            return TerrainKind.Crater;

        if (InTerrainEllipse(x,z,-7f,14f,7.8f,6.6f) ||
            InTerrainEllipse(x,z,12f,1f,7.2f,5.8f))
            return TerrainKind.Rough;

        return TerrainKind.Plain;
    }

    float TerrainTraversalMultiplierAt(float x, float z)
    {
        TerrainKind kind = TerrainKindAt(x,z);
        if (kind == TerrainKind.Rough) return 1.28f;
        if (kind == TerrainKind.Crater) return 1.62f;
        return 1f;
    }

    float TerrainRoutePlanningCostAt(float x, float z)
    {
        // Грунт влияет на скорость и батарею, но маршрут выбирается по геометрии.
        return 1f;
    }

    float TerrainSpeedMultiplierAt(float x, float z)
    {
        TerrainKind kind = TerrainKindAt(x,z);
        if (kind == TerrainKind.Rough) return .78f;
        if (kind == TerrainKind.Crater) return .58f;
        return 1f;
    }

    private const float PathMinX = -31f;
    private const float PathMaxX = 31f;
    private const float PathMinZ = -19f;
    private const float PathMaxZ = 19f;
    private const float PathStep = 1.15f;
    // Колёса имеют нижнюю точку примерно -0.06 относительно root.
    // 0.085 оставляет несколько миллиметров над Mesh и визуально ставит их на грунт.
    private const float RoverGroundOffset = -.165f;

    // UI
    private Text dayText, creditsText, scoreText, deliveriesText;
    private Text roverNameText, roverStatusText, roverDetailsText;
    private Text orderNameText, orderDetailsText, routeText, eventText;
    private Image batteryFill, riskFill;
    private Button launchButton, serviceButton, nextDayButton;
    private Text launchText, serviceText, nextDayButtonText;
    private GameObject startOverlay, summaryOverlay, toastPanel, pauseOverlay;
    private GameObject pauseMainPanel, pauseSettingsPanel;
    private Text toastText, summaryText, summaryTitle, summaryActionText;
    private Text displayModeText, resolutionText, pauseStatusText;
    private Button summaryActionButton;
    private float toastTimer;
    private bool shiftStarted;
    private bool pauseMenuOpen;
    private bool shiftStartedBeforePause;
    private int displayModeIndex;
    private int resolutionIndex;

    private readonly Vector2Int[] displayResolutions =
    {
        new Vector2Int(1280,720),
        new Vector2Int(1600,900),
        new Vector2Int(1920,1080),
        new Vector2Int(2560,1440)
    };

    // Palette
    private readonly Color bg = Hex("071019");
    private readonly Color panel = Hex("0C1722");
    private readonly Color card = Hex("111F2D");
    private readonly Color card2 = Hex("152638");
    private readonly Color cyan = Hex("4BD7FF");
    private readonly Color green = Hex("55E6A5");
    private readonly Color amber = Hex("FFBE55");
    private readonly Color red = Hex("FF667A");
    private readonly Color text = Hex("EAF5FF");
    private readonly Color muted = Hex("8297AA");

    void Start()
    {
        Application.targetFrameRate = 120;
        QualitySettings.antiAliasing = 4;
        RenderSettings.fog = false;
        SetupInputSystem();
        InitDisplaySettings();
        LoadRuntimeShaders();
        BuildWorld();
        BuildUI();
        LoadOrCreate();
        SpawnGameObjects();
        RefreshUI();
        ShowStartScreen();
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            TogglePauseMenu();
#else
        if (Input.GetKeyDown(KeyCode.Escape))
            TogglePauseMenu();
#endif

        // При открытом ESC-меню игровое время стоит.
        if (!pauseMenuOpen)
            AnimateWorld();

        if (toastPanel != null && toastPanel.activeSelf)
        {
            toastTimer -= Time.deltaTime;
            if (toastTimer <= 0f) toastPanel.SetActive(false);
        }

        if (pauseMenuOpen || !shiftStarted || deliveryAnimating || cam == null) return;

        bool pressed = false;
        Vector2 pointer = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            pressed = true;
            pointer = Mouse.current.position.ReadValue();
        }
#else
        if (Input.GetMouseButtonDown(0))
        {
            pressed = true;
            pointer = Input.mousePosition;
        }
#endif
        if (!pressed) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        Ray ray = cam.ScreenPointToRay(pointer);
        if (Physics.Raycast(ray, out RaycastHit hit, 200f))
        {
            var pick = hit.collider.GetComponentInParent<MapSelectable>();
            if (pick != null) Pick(pick.id, pick.isRover);
        }
    }

    void SetupInputSystem()
    {
        EventSystem es = FindFirstObjectByType<EventSystem>();
        if (es == null)
        {
            var go = new GameObject("EventSystem");
            es = go.AddComponent<EventSystem>();
        }

#if ENABLE_INPUT_SYSTEM
        var legacy = es.GetComponent<StandaloneInputModule>();
        if (legacy != null) DestroyImmediate(legacy);
        if (es.GetComponent<InputSystemUIInputModule>() == null)
            es.gameObject.AddComponent<InputSystemUIInputModule>();
#else
        if (es.GetComponent<StandaloneInputModule>() == null)
            es.gameObject.AddComponent<StandaloneInputModule>();
#endif
    }

    void BuildWorld()
    {
        cam = Camera.main;
        if (cam == null)
        {
            var c = new GameObject("Main Camera");
            cam = c.AddComponent<Camera>();
            c.tag = "MainCamera";
        }

        cam.transform.position = new Vector3(0, 25.5f, -26.5f);
        cam.transform.rotation = Quaternion.Euler(46f, 0f, 0f);
        cam.fieldOfView = 47f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Hex("02060B");

        RenderSettings.ambientLight = new Color(.040f, .045f, .055f);
        var lightGo = new GameObject("Moon Sun");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.34f;
        light.color = new Color(.92f, .94f, 1f);
        // На Луне нет атмосферы, поэтому тени жёсткие и контрастные.
        light.shadows = LightShadows.Hard;
        light.shadowStrength = .92f;
        lightGo.transform.rotation = Quaternion.Euler(29f, -47f, 0f);

        routeObstacleCenters.Clear();
        routeObstacleRadii.Clear();
        craterSpecs.Clear();
        outcropSpecs.Clear();

        // Кратеры и каменные гребни являются частью одной поверхности.
        // Все крупные формы сразу регистрируются в A*, поэтому маршрут
        // автоматически огибает реальные препятствия, которые видит игрок.
        GenerateCraterSpecs();
        GenerateOutcropSpecs();
        BuildMoonSurface();

        BuildTerrainDetails();
        BuildLandingLanes();
        BuildBase();

        // Линия маршрута намеренно не показывается игроку.
        // Путь всё равно рассчитывается A* и используется для движения/расхода батареи.
        routeLine = null;
    }

    void AddCrater(float x, float z, float radius, float depth, bool blocksRoute)
    {
        craterSpecs.Add(new CraterSpec
        {
            center = new Vector2(x,z),
            radius = radius,
            depth = depth
        });

        if (blocksRoute)
            AddRouteObstacle(new Vector2(x,z), radius * .58f);
    }

    void GenerateCraterSpecs()
    {
        var rnd = new System.Random(44);

        // GREEN O1 — clean corridor.
        AddCrater(-19.1f, 12.2f, .56f, .07f, false);

        // YELLOW O5 (22%) — one mandatory local detour.
        AddCrater( 7.15f, -0.95f, 1.58f, .44f, true);
        AddCrater(10.4f,   2.5f,  .92f, .18f, false);
        AddCrater(14.2f,   2.4f,  .70f, .12f, false);

        // YELLOW O2 (28%) — two staggered blocking craters.
        AddCrater(-4.70f,  7.75f, 1.82f, .56f, true);
        AddCrater(-6.10f, 10.65f, 1.74f, .51f, true);
        AddCrater(-9.7f,  12.6f, 1.42f, .34f, false);
        AddCrater(-4.8f,  16.4f, 1.34f, .30f, false);
        AddCrater(-9.0f,  17.0f, 1.10f, .23f, false);
        AddCrater(-7.5f,  14.0f,  .86f, .16f, false);

        // RED O4 (42%) — dense crater sector; also impossible by 90 kg weight.
        AddCrater(10.7f,  6.8f, 2.22f, .88f, true);
        AddCrater(13.0f,  8.5f, 1.88f, .72f, true);
        AddCrater(18.0f,  9.5f, 2.12f, .84f, true);
        AddCrater(16.8f, 14.2f, 1.78f, .66f, true);
        AddCrater(13.6f, 13.5f, .76f, .13f, false);
        AddCrater(17.6f, 12.1f, .66f, .11f, false);

        // RED O3 (52%) — hardest feasible sector.
        AddCrater( 3.0f, 10.3f, 2.35f, .96f, true);
        AddCrater( 1.3f, 12.9f, 2.15f, .86f, true);
        AddCrater( 6.8f, 12.7f, 2.05f, .82f, true);
        AddCrater( 1.1f, 17.4f, 1.92f, .76f, true);
        AddCrater( 6.6f, 17.2f, 1.82f, .71f, true);
        AddCrater( 3.4f, 13.6f, .72f, .13f, false);
        AddCrater( 5.1f, 16.1f, .64f, .11f, false);

        // Sparse background cratering.
        int created = 0;
        for (int attempt = 0; attempt < 340 && created < 20; attempt++)
        {
            float x = (float)(rnd.NextDouble() * 66 - 33);
            float z = (float)(rnd.NextDouble() * 43 - 15);
            if (IsReservedSpot(x,z)) continue;

            bool nearManual = false;
            foreach (CraterSpec existing in craterSpecs)
            {
                if (Vector2.Distance(new Vector2(x,z),existing.center) <
                    existing.radius + .95f)
                {
                    nearManual = true;
                    break;
                }
            }
            if (nearManual) continue;

            TerrainKind kind = TerrainKindAt(x,z);
            float accept =
                kind == TerrainKind.Plain ? .025f :
                kind == TerrainKind.Rough ? .085f : .18f;
            if (rnd.NextDouble() > accept) continue;

            float radius;
            float depth;

            if (kind == TerrainKind.Crater)
            {
                radius = (float)(rnd.NextDouble() * .58 + .40);
                depth = Mathf.Lerp(.055f,.18f,Mathf.InverseLerp(.40f,.98f,radius));
            }
            else if (kind == TerrainKind.Rough)
            {
                radius = (float)(rnd.NextDouble() * .36 + .28);
                depth = Mathf.Lerp(.03f,.10f,Mathf.InverseLerp(.28f,.64f,radius));
            }
            else
            {
                radius = (float)(rnd.NextDouble() * .20 + .16);
                depth = Mathf.Lerp(.015f,.04f,Mathf.InverseLerp(.16f,.36f,radius));
            }

            AddCrater(x,z,radius,depth,false);
            created++;
        }
    }

    void GenerateOutcropSpecs()
    {
        outcropSpecs.Clear();
    }

    float TerrainHeightAt(float x, float z)
    {
        // Низкоамплитудный непрерывный рельеф реголита.
        float macro  = Mathf.PerlinNoise((x + 174f) * .018f, (z + 113f) * .018f) - .5f;
        float broad  = Mathf.PerlinNoise((x +  61f) * .043f, (z +  33f) * .043f) - .5f;
        float medium = Mathf.PerlinNoise((x +  24f) * .105f, (z +  59f) * .105f) - .5f;
        float fine   = Mathf.PerlinNoise((x +  13f) * .29f,  (z +  21f) * .29f)  - .5f;

        float h = -0.055f
                + macro  * .038f
                + broad  * .024f
                + medium * .008f
                + fine   * .0025f;

        // Очень широкие плавные бассейны — геометрическая основа лунных морей.
        float mareA = Mathf.Clamp01(1f - Mathf.Sqrt(
            Mathf.Pow((x + 15f)/19.5f,2f) + Mathf.Pow((z - 4f)/13.2f,2f)
        ));
        float mareB = Mathf.Clamp01(1f - Mathf.Sqrt(
            Mathf.Pow((x - 20f)/16.0f,2f) + Mathf.Pow((z + 1f)/10.5f,2f)
        ));

        h -= mareA * mareA * .024f;
        h -= mareB * mareB * .018f;

        // Настоящие чаши кратеров.
        foreach (CraterSpec c in craterSpecs)
        {
            float d = Vector2.Distance(new Vector2(x,z),c.center);
            float n = d / Mathf.Max(.01f,c.radius);

            if (n < 1f)
            {
                float bowl = 1f - n*n;
                h -= c.depth * Mathf.Pow(bowl,1.22f);
            }
            else if (n < 1.28f)
            {
                float rim = 1f - Mathf.Abs((n - 1.075f) / .185f);
                rim = Mathf.Clamp01(rim);
                h += rim * rim * c.depth * .085f;
            }
        }

        // Плавно выровненная площадка базы.
        float bx = x / 8.8f;
        float bz = (z + 8.25f) / 5.5f;
        float baseMask = Mathf.Clamp01(1f - Mathf.Sqrt(bx*bx + bz*bz));
        baseMask = baseMask * baseMask * .86f;
        h = Mathf.Lerp(h,-.048f,baseMask);

        return h;
    }

    void BuildMoonSurface()
    {
        const int xSegments = 244;
        const int zSegments = 208;
        const float width = 92f;
        const float depth = 82f;
        const float centerZ = 13f;

        var go = new GameObject("Лунная поверхность");
        var filter = go.AddComponent<MeshFilter>();
        var renderer = go.AddComponent<MeshRenderer>();

        int vx = xSegments + 1;
        int vz = zSegments + 1;
        var vertices = new Vector3[vx * vz];
        var uvs = new Vector2[vertices.Length];

        for (int z = 0; z < vz; z++)
        {
            float tz = z / (float)zSegments;
            float worldZ = centerZ - depth * .5f + tz * depth;

            for (int x = 0; x < vx; x++)
            {
                float tx = x / (float)xSegments;
                float worldX = -width * .5f + tx * width;
                float y = TerrainHeightAt(worldX,worldZ);

                int idx = z * vx + x;
                vertices[idx] = new Vector3(worldX,y,worldZ);
                uvs[idx] = new Vector2(tx,tz);
            }
        }

        var triangles = new int[xSegments * zSegments * 6];
        int ti = 0;
        for (int z = 0; z < zSegments; z++)
        for (int x = 0; x < xSegments; x++)
        {
            int i0 = z * vx + x;
            int i1 = i0 + 1;
            int i2 = i0 + vx;
            int i3 = i2 + 1;

            triangles[ti++] = i0;
            triangles[ti++] = i2;
            triangles[ti++] = i1;
            triangles[ti++] = i1;
            triangles[ti++] = i2;
            triangles[ti++] = i3;
        }

        var mesh = new Mesh();
        mesh.name = "Moon Terrain Mesh";
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        filter.sharedMesh = mesh;

        SetTerrainMaterial(go, Hex("B9BEC6"));
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = true;
    }

    void BuildLandingLanes()
    {
        // Три одинаковые парковочные станции — по одной под каждым ровером.
        for (int i = -1; i <= 1; i++)
        {
            float x = i * 4f;
            float z = -3.2f;
            float groundY = TerrainHeightAt(x,z);

            var pad = Primitive(PrimitiveType.Cube, null, "Парковочная станция", Vector3.zero,
                new Vector3(3.35f,.025f,4.25f), Hex("18222B"));
            pad.transform.position = new Vector3(x,groundY + .004f,z);

            var inner = Primitive(PrimitiveType.Cube, null, "Центр парковочной станции", Vector3.zero,
                new Vector3(2.70f,.012f,3.55f), Hex("25323C"));
            inner.transform.position = new Vector3(x,groundY + .020f,z);

            // Небольшая циановая полоса спереди станции.
            var stripe = Primitive(PrimitiveType.Cube, null, "Маркер станции", Vector3.zero,
                new Vector3(2.25f,.010f,.10f), new Color(cyan.r,cyan.g,cyan.b,.85f));
            stripe.transform.position = new Vector3(x,groundY + .029f,z - 1.72f);

            // Четыре угловых огня на каждой станции.
            float[] xs = { -1.42f, 1.42f };
            float[] zs = { -1.82f, 1.82f };
            foreach (float dx in xs)
            foreach (float dz in zs)
            {
                var lamp = Primitive(PrimitiveType.Sphere, null, "Огонь парковочной станции", Vector3.zero,
                    new Vector3(.075f,.075f,.075f), new Color(cyan.r,cyan.g,cyan.b,.95f));
                lamp.transform.position = new Vector3(x + dx,groundY + .16f,z + dz);
            }
        }

    }

    void BuildHorizon()
    {
        // Огромная поверхность сама заполняет кадр.
    }

    // Старый BuildCraters больше не создаёт геометрию поверх грунта.
    // Кратеры уже встроены в Mesh поверхности как настоящие углубления.
    void BuildCraters() { }

    void BuildBase()
    {
        var root = new GameObject("База Артемида");

        const float baseX = 0f;
        const float baseZ = -8.25f;
        float groundY = TerrainHeightAt(baseX,baseZ);
        root.transform.position = new Vector3(baseX,groundY,baseZ);

        Color hull       = Hex("D7DDDF");
        Color hullDark   = Hex("9CA9AF");
        Color metal      = Hex("66747B");
        Color dark       = Hex("202B31");
        Color glass      = Hex("123E55");
        Color solar      = Hex("0E3551");
        Color solarGrid  = Hex("4D7384");
        Color gold       = Hex("B88A32");

        // ============================================================
        // ЕДИНАЯ ОСНОВА
        // Вся станция стоит на одной платформе — никаких висящих частей.
        // ============================================================
        Primitive(PrimitiveType.Cube,root.transform,"Несущая платформа",
            new Vector3(0,.055f,0),
            new Vector3(6.35f,.11f,3.45f),dark);

        Primitive(PrimitiveType.Cube,root.transform,"Верхняя палуба",
            new Vector3(0,.125f,0),
            new Vector3(6.05f,.035f,3.15f),Hex("465158"));

        // ============================================================
        // ОСНОВНОЙ КОРПУС — ОДНА СПЛОШНАЯ ГЕРМЕТИЧНАЯ СЕКЦИЯ
        // Это специально одна большая деталь, чтобы база не выглядела
        // как набор несоединённых коробок.
        // ============================================================
        Primitive(PrimitiveType.Cube,root.transform,"Основной корпус",
            new Vector3(0,.68f,-.12f),
            new Vector3(5.05f,.86f,1.62f),hullDark);

        // Центральная цилиндрическая секция врезана прямо в корпус.
        Primitive(PrimitiveType.Cylinder,root.transform,"Центральный гермомодуль",
            new Vector3(0,.83f,-.10f),
            new Vector3(1.88f,.54f,1.88f),hull);

        Primitive(PrimitiveType.Cylinder,root.transform,"Теплоизоляционный пояс",
            new Vector3(0,.47f,-.10f),
            new Vector3(1.96f,.065f,1.96f),gold);

        // Купол сверху.
        Primitive(PrimitiveType.Sphere,root.transform,"Командный купол",
            new Vector3(0,1.32f,-.10f),
            new Vector3(1.34f,.48f,1.34f),hull);

        // Центральное панорамное окно на стороне роверов.
        Primitive(PrimitiveType.Cube,root.transform,"Панорамное окно",
            new Vector3(0,.92f,.835f),
            new Vector3(1.20f,.22f,.035f),glass);

        // Боковые окна встроены в тот же корпус.
        for (int side=-1; side<=1; side+=2)
        {
            Primitive(PrimitiveType.Cube,root.transform,"Окно бокового модуля",
                new Vector3(side*1.80f,.76f,.715f),
                new Vector3(.78f,.20f,.035f),glass);

            // Техническая панель снизу создаёт цельный индустриальный силуэт.
            Primitive(PrimitiveType.Cube,root.transform,"Техническая панель",
                new Vector3(side*1.82f,.30f,-.13f),
                new Vector3(1.00f,.12f,1.50f),dark);

            // Маленькая золотая теплоизоляционная полоса.
            Primitive(PrimitiveType.Cube,root.transform,"Теплоизоляционная полоса",
                new Vector3(side*1.82f,1.04f,-.13f),
                new Vector3(1.02f,.055f,1.50f),gold);
        }

        // ============================================================
        // ШЛЮЗ — ВСТРОЕН В ОСНОВНОЙ КОРПУС И НАПРАВЛЕН К РОВЕРАМ
        // ============================================================
        Primitive(PrimitiveType.Cube,root.transform,"Шлюзовой тоннель",
            new Vector3(0,.65f,1.18f),
            new Vector3(.96f,.68f,1.20f),hull);

        Primitive(PrimitiveType.Cube,root.transform,"Рама шлюза",
            new Vector3(0,.65f,1.79f),
            new Vector3(1.08f,.78f,.12f),metal);

        Primitive(PrimitiveType.Cube,root.transform,"Наружный люк",
            new Vector3(0,.65f,1.865f),
            new Vector3(.68f,.52f,.035f),dark);

        Primitive(PrimitiveType.Cube,root.transform,"Маркер шлюза",
            new Vector3(0,.34f,1.895f),
            new Vector3(.72f,.06f,.025f),gold);

        // ============================================================
        // СОЛНЕЧНЫЕ КРЫЛЬЯ
        // Теперь это длинные настоящие массивы СЛЕВА/СПРАВА,
        // соединённые с корпусом горизонтальными силовыми штангами.
        // ============================================================
        for (int side=-1; side<=1; side+=2)
        {
            float boomX = side*3.10f;
            float panelX = side*4.55f;
            float panelZ = -.28f;

            // Штанга реально касается корпуса и рамы панели.
            Primitive(PrimitiveType.Cube,root.transform,"Силовая штанга",
                new Vector3(boomX,.34f,panelZ),
                new Vector3(1.35f,.09f,.13f),metal);

            // Поворотный узел.
            Primitive(PrimitiveType.Cylinder,root.transform,"Поворотный узел панели",
                new Vector3(side*3.76f,.34f,panelZ),
                new Vector3(.16f,.12f,.16f),metal);

            // Общая рама солнечного крыла.
            Primitive(PrimitiveType.Cube,root.transform,"Рама солнечного крыла",
                new Vector3(panelX,.30f,panelZ),
                new Vector3(2.55f,.045f,1.48f),dark);

            // 4 x 2 крупных фотоэлемента.
            for (int row=0; row<2; row++)
            for (int col=0; col<4; col++)
            {
                float px = panelX + (col-1.5f)*.56f;
                float pz = panelZ + (row-.5f)*.56f;

                Primitive(PrimitiveType.Cube,root.transform,"Фотоэлемент",
                    new Vector3(px,.335f,pz),
                    new Vector3(.49f,.022f,.48f),solar);
            }

            // Видимая центральная линия разделения секций.
            Primitive(PrimitiveType.Cube,root.transform,"Шина солнечной панели",
                new Vector3(panelX,.365f,panelZ),
                new Vector3(2.30f,.018f,.045f),solarGrid);
        }

        // ============================================================
        // АНТЕННА И СВЯЗЬ
        // ============================================================
        Primitive(PrimitiveType.Cylinder,root.transform,"Мачта связи",
            new Vector3(.42f,1.78f,-.20f),
            new Vector3(.060f,.30f,.060f),metal);

        var dish = Primitive(PrimitiveType.Cylinder,root.transform,"Антенна дальней связи",
            new Vector3(.42f,2.08f,-.20f),
            new Vector3(.43f,.040f,.43f),hull);
        dish.transform.localRotation = Quaternion.Euler(18f,0f,10f);

        Primitive(PrimitiveType.Cylinder,root.transform,"Облучатель антенны",
            new Vector3(.42f,2.25f,-.28f),
            new Vector3(.035f,.115f,.035f),dark);

        // Два компактных маяка на корпусе.
        for (int side=-1; side<=1; side+=2)
        {
            var lamp = Primitive(PrimitiveType.Sphere,root.transform,"Маяк базы",
                new Vector3(side*2.20f,1.15f,.36f),
                new Vector3(.060f,.060f,.060f),cyan);

            var light = lamp.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 2.4f;
            light.intensity = .45f;
            light.color = cyan;
        }

        // Навигация учитывает сам герметичный корпус.
        // Солнечные крылья расположены по бокам и не перекрывают стартовые коридоры.
        AddRouteObstacle(new Vector2(baseX,baseZ),3.10f);
    }

    void BuildTerrainDetails()
    {
        // В этой версии нет отдельных сфер-"камней".
        // Весь мелкий лунный рельеф встроен непосредственно в Mesh поверхности.
        // Так грунт выглядит цельным, а не как плоскость с чёрными шариками сверху.
    }

    void AddRouteObstacle(Vector2 center, float radius)
    {
        routeObstacleCenters.Add(center);
        routeObstacleRadii.Add(radius);
    }

    bool IsReservedSpot(float x, float z)
    {
        Vector2 p = new Vector2(x,z);
        Vector2[] reserved =
        {
            new Vector2(-4f,-3.2f), new Vector2(0f,-3.2f), new Vector2(4f,-3.2f),
            new Vector2(0f,-8.25f),
            new Vector2(-16f,9f), new Vector2(-7f,14f), new Vector2(4f,15f),
            new Vector2(15f,11f), new Vector2(12f,1f)
        };
        foreach (Vector2 r in reserved)
            if (Vector2.Distance(p,r) < 3.1f) return true;
        return false;
    }

    void CreateZone(string name, Vector3 pos, Vector3 scale, Color color)
    {
        var zone = GameObject.CreatePrimitive(PrimitiveType.Cube);
        zone.name = name;
        zone.transform.position = pos;
        zone.transform.localScale = scale;
        SetMaterial(zone, color, .03f, 0f);
        Destroy(zone.GetComponent<Collider>());
    }

    void CreateGroundLine(Vector3 a, Vector3 b, Color color)
    {
        var go = new GameObject("Grid Line");
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);
        lr.widthMultiplier = .025f;
        var lineMat = NewLineMaterial(color); if (lineMat != null) lr.material = lineMat;
        lr.startColor = lr.endColor = color;
    }

    void BuildUI()
    {
        var canvasGo = new GameObject("HUD");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920,1080);
        scaler.matchWidthOrHeight = .5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        // Верхняя панель.
        var top = MakePanel(canvas.transform, "Верхняя панель", Vector2.zero, Vector2.zero,
            new Vector2(0,1), new Vector2(1,1), new Color(bg.r,bg.g,bg.b,.96f));
        StretchX(top.GetComponent<RectTransform>(), 0,0,0,78);
        AddAccentLine(top.transform, new Color(cyan.r,cyan.g,cyan.b,.75f), true);

        var brand = MakeText(top.transform, "MOON COURIER CRISIS", 26, FontStyle.Bold, text);
        SetRect(brand.rectTransform, 28,-13,390,34,new Vector2(0,1));
        var sub = MakeText(top.transform, "ЛУННАЯ ЛОГИСТИКА  //  БАЗА «АРТЕМИДА»", 11, FontStyle.Bold, cyan);
        SetRect(sub.rectTransform, 30,-48,420,20,new Vector2(0,1));

        CreateTopStat(top.transform, "ДЕНЬ", out dayText, 770);
        CreateTopStat(top.transform, "КРЕДИТЫ", out creditsText, 915);
        CreateTopStat(top.transform, "ОЧКИ", out scoreText, 1060);
        CreateTopStat(top.transform, "ДОСТАВКИ", out deliveriesText, 1205);

        nextDayButton = MakeButton(top.transform, "ЗАВЕРШИТЬ ДЕНЬ", Vector2.zero, new Vector2(158,42),
            NextDay, Hex("14314A"), text);
        AnchorRight(nextDayButton.GetComponent<RectTransform>(), 142,17,158,42);
        nextDayButtonText = nextDayButton.GetComponentInChildren<Text>();
        var reset = MakeButton(top.transform, "НОВАЯ ИГРА", Vector2.zero, new Vector2(112,42),
            NewGame, Hex("101B25"), muted);
        AnchorRight(reset.GetComponent<RectTransform>(), 18,17,112,42);

        // Левая компактная карточка ровера.
        var left = MakePanel(canvas.transform, "Карточка ровера", new Vector2(24,-102), new Vector2(300,620),
            new Vector2(0,1), new Vector2(0,1), new Color(panel.r,panel.g,panel.b,.96f));
        AddAccentLine(left.transform, cyan, false);
        MakeSectionLabel(left.transform, "ВЫБРАННЫЙ РОВЕР", 20,-18);

        roverNameText = MakeText(left.transform, "РОВЕР НЕ ВЫБРАН", 24, FontStyle.Bold, text);
        SetRect(roverNameText.rectTransform, 20,-55,260,36,new Vector2(0,1));
        roverStatusText = MakePill(left.transform, "НЕ ВЫБРАН", 20,-100,140);

        MakeSmallLabel(left.transform, "ЗАРЯД БАТАРЕИ", 20,-150);
        batteryFill = MakeBar(left.transform,20,-176,260,12,cyan);

        roverDetailsText = MakeText(left.transform, "", 15, FontStyle.Normal, muted);
        roverDetailsText.lineSpacing = 1.24f;
        SetRect(roverDetailsText.rectTransform,20,-215,260,170,new Vector2(0,1));

        MakeButton(left.transform,"СЛЕДУЮЩИЙ РОВЕР",new Vector2(20,-405),new Vector2(260,50),
            CycleRover,card2,text);
        serviceButton = MakeButton(left.transform,"ДИАГНОСТИКА",new Vector2(20,-469),new Vector2(260,50),
            ServiceRover,Hex("172735"),text);
        serviceText = serviceButton.GetComponentInChildren<Text>();

        var hint = MakeText(left.transform,
            "Совет: лёгкий ровер экономичнее, тяжёлый — берёт больше груза.",12,FontStyle.Normal,muted);
        SetRect(hint.rectTransform,20,-542,260,58,new Vector2(0,1));

        // Правая карточка заказа.
        var right = MakePanel(canvas.transform,"Карточка заказа",new Vector2(-24,-102),new Vector2(320,620),
            new Vector2(1,1),new Vector2(1,1),new Color(panel.r,panel.g,panel.b,.96f));
        right.GetComponent<RectTransform>().pivot = new Vector2(1,1);
        AddAccentLine(right.transform,amber,false);
        MakeSectionLabel(right.transform,"ЗАДАНИЕ НА ДОСТАВКУ",20,-18);

        orderNameText = MakeText(right.transform,"ЗАКАЗ НЕ ВЫБРАН",22,FontStyle.Bold,text);
        SetRect(orderNameText.rectTransform,20,-55,280,60,new Vector2(0,1));

        MakeSmallLabel(right.transform,"РИСК МАРШРУТА",20,-133);
        riskFill = MakeBar(right.transform,20,-160,280,12,red);

        orderDetailsText = MakeText(right.transform,"",15,FontStyle.Normal,muted);
        orderDetailsText.lineSpacing = 1.22f;
        SetRect(orderDetailsText.rectTransform,20,-200,280,145,new Vector2(0,1));

        MakeButton(right.transform,"СЛЕДУЮЩИЙ ЗАКАЗ",new Vector2(20,-360),new Vector2(280,50),
            CycleOrder,card2,text);

        var routeCard = MakePanel(right.transform,"Расчёт маршрута",new Vector2(20,-424),new Vector2(280,105),
            new Vector2(0,1),new Vector2(0,1),Hex("102130"));
        routeText = MakeText(routeCard.transform,"ВЫБЕРИТЕ РОВЕР И ЗАКАЗ",14,FontStyle.Bold,muted);
        routeText.alignment = TextAnchor.MiddleCenter;
        Stretch(routeText.rectTransform,12,12,12,12);

        launchButton = MakeButton(right.transform,"ОТПРАВИТЬ РОВЕР",new Vector2(20,-544),new Vector2(280,56),
            StartDelivery,Hex("0B6784"),text);
        launchText = launchButton.GetComponentInChildren<Text>();

        // Мини-легенда поверх карты.
        var legendPanel = MakePanel(canvas.transform,"Легенда",new Vector2(0,-94),new Vector2(580,34),
            new Vector2(.5f,1),new Vector2(.5f,1),new Color(panel.r,panel.g,panel.b,.88f));
        legendPanel.GetComponent<RectTransform>().pivot = new Vector2(.5f,1);
        var legend = MakeText(legendPanel.transform,
            "РАВНИНА ×1.00   •   НЕРОВНЫЙ ГРУНТ ×1.28   •   КРАТЕРНОЕ ПОЛЕ ×1.62",
            10,FontStyle.Bold,muted);
        legend.alignment = TextAnchor.MiddleCenter;
        Stretch(legend.rectTransform,8,8,4,4);

        // Журнал — компактная панель внизу карты.
        var bottom = MakePanel(canvas.transform,"Журнал",Vector2.zero,Vector2.zero,
            new Vector2(0,0),new Vector2(1,0),new Color(panel.r,panel.g,panel.b,.93f));
        var brt = bottom.GetComponent<RectTransform>();
        brt.anchorMin = new Vector2(0,0); brt.anchorMax = new Vector2(1,0); brt.pivot = new Vector2(.5f,0);
        brt.offsetMin = new Vector2(344,20); brt.offsetMax = new Vector2(-364,108);
        MakeSectionLabel(bottom.transform,"ЖУРНАЛ СМЕНЫ",18,-10);
        eventText = MakeText(bottom.transform,"",12,FontStyle.Normal,text);
        eventText.lineSpacing = 1.15f;
        Stretch(eventText.rectTransform,18,18,12,34);

        // Всплывающее уведомление: результат действия виден даже если игрок не смотрит в журнал.
        // Сообщение НЕ обрезается по числу символов: длинный текст переносится на 2–3 строки.
        toastPanel = MakePanel(canvas.transform,"Уведомление",new Vector2(0,-145),new Vector2(720,76),
            new Vector2(.5f,1),new Vector2(.5f,1),new Color(card.r,card.g,card.b,.98f));
        toastPanel.GetComponent<RectTransform>().pivot = new Vector2(.5f,1);
        toastText = MakeText(toastPanel.transform,"",14,FontStyle.Bold,text);
        toastText.alignment = TextAnchor.MiddleCenter;
        toastText.horizontalOverflow = HorizontalWrapMode.Wrap;
        toastText.verticalOverflow = VerticalWrapMode.Overflow;
        toastText.resizeTextForBestFit = true;
        toastText.resizeTextMinSize = 11;
        toastText.resizeTextMaxSize = 14;
        toastText.lineSpacing = 1.0f;
        Stretch(toastText.rectTransform,16,16,8,8);
        toastPanel.SetActive(false);

        BuildStartOverlay(canvas.transform);
        BuildSummaryOverlay(canvas.transform);
        BuildPauseOverlay(canvas.transform);
    }

    void InitDisplaySettings()
    {
        displayModeIndex =
            Screen.fullScreenMode == FullScreenMode.Windowed ? 0 : 1;

        resolutionIndex = 0;
        int bestDistance = int.MaxValue;

        for (int i=0; i<displayResolutions.Length; i++)
        {
            int d =
                Mathf.Abs(displayResolutions[i].x - Screen.width) +
                Mathf.Abs(displayResolutions[i].y - Screen.height);

            if (d < bestDistance)
            {
                bestDistance = d;
                resolutionIndex = i;
            }
        }
    }

    void BuildPauseOverlay(Transform parent)
    {
        pauseOverlay = MakePanel(parent,"ESC меню",Vector2.zero,Vector2.zero,
            Vector2.zero,Vector2.one,new Color(bg.r,bg.g,bg.b,.94f));
        Stretch(pauseOverlay.GetComponent<RectTransform>(),0,0,0,0);

        pauseMainPanel = MakePanel(pauseOverlay.transform,"Главное меню паузы",
            new Vector2(0,-225),new Vector2(560,500),
            new Vector2(.5f,1),new Vector2(.5f,1),
            new Color(panel.r,panel.g,panel.b,.99f));
        pauseMainPanel.GetComponent<RectTransform>().pivot = new Vector2(.5f,1);
        AddAccentLine(pauseMainPanel.transform,cyan,false);

        var title = MakeText(pauseMainPanel.transform,"ПАУЗА",38,FontStyle.Bold,text);
        title.alignment = TextAnchor.MiddleCenter;
        SetRect(title.rectTransform,0,-42,460,58,new Vector2(.5f,1));

        var resume = MakeButton(pauseMainPanel.transform,"ПРОДОЛЖИТЬ",
            Vector2.zero,new Vector2(350,58),
            ClosePauseMenu,Hex("17394D"),text);
        SetRect(resume.GetComponent<RectTransform>(),0,-145,350,58,new Vector2(.5f,1));

        var settings = MakeButton(pauseMainPanel.transform,"НАСТРОЙКИ",
            Vector2.zero,new Vector2(350,58),
            ShowSettingsPage,Hex("17394D"),text);
        SetRect(settings.GetComponent<RectTransform>(),0,-225,350,58,new Vector2(.5f,1));

        var quit = MakeButton(pauseMainPanel.transform,"ВЫЙТИ ИЗ ИГРЫ",
            Vector2.zero,new Vector2(350,58),
            QuitGame,Hex("17394D"),text);
        SetRect(quit.GetComponent<RectTransform>(),0,-305,350,58,new Vector2(.5f,1));

        var saveHint = MakeText(pauseMainPanel.transform,
            "Перед выходом текущая смена сохраняется.",
            11,FontStyle.Normal,muted);
        saveHint.alignment = TextAnchor.MiddleCenter;
        SetRect(saveHint.rectTransform,0,-385,420,24,new Vector2(.5f,1));

        pauseSettingsPanel = MakePanel(pauseOverlay.transform,"Настройки",
            new Vector2(0,-225),new Vector2(620,510),
            new Vector2(.5f,1),new Vector2(.5f,1),
            new Color(panel.r,panel.g,panel.b,.99f));
        pauseSettingsPanel.GetComponent<RectTransform>().pivot = new Vector2(.5f,1);
        AddAccentLine(pauseSettingsPanel.transform,cyan,false);

        var settingsTitle = MakeText(pauseSettingsPanel.transform,"НАСТРОЙКИ",34,FontStyle.Bold,text);
        settingsTitle.alignment = TextAnchor.MiddleCenter;
        SetRect(settingsTitle.rectTransform,0,-34,520,54,new Vector2(.5f,1));

        MakeSmallLabel(pauseSettingsPanel.transform,"РЕЖИМ ЭКРАНА",120,-116);
        var modeButton = MakeButton(pauseSettingsPanel.transform,"",
            Vector2.zero,new Vector2(390,52),
            CycleDisplayMode,card2,text);
        SetRect(modeButton.GetComponent<RectTransform>(),0,-145,390,52,new Vector2(.5f,1));
        displayModeText = modeButton.GetComponentInChildren<Text>();

        MakeSmallLabel(pauseSettingsPanel.transform,"РАЗРЕШЕНИЕ",120,-218);
        var resolutionButton = MakeButton(pauseSettingsPanel.transform,"",
            Vector2.zero,new Vector2(390,52),
            CycleResolution,card2,text);
        SetRect(resolutionButton.GetComponent<RectTransform>(),0,-247,390,52,new Vector2(.5f,1));
        resolutionText = resolutionButton.GetComponentInChildren<Text>();

        var apply = MakeButton(pauseSettingsPanel.transform,"ПРИМЕНИТЬ",
            Vector2.zero,new Vector2(390,54),
            ApplyDisplaySettings,Hex("0B708E"),text);
        SetRect(apply.GetComponent<RectTransform>(),0,-330,390,54,new Vector2(.5f,1));

        pauseStatusText = MakeText(pauseSettingsPanel.transform,"",11,FontStyle.Bold,green);
        pauseStatusText.alignment = TextAnchor.MiddleCenter;
        SetRect(pauseStatusText.rectTransform,0,-391,470,24,new Vector2(.5f,1));

        var back = MakeButton(pauseSettingsPanel.transform,"НАЗАД",
            Vector2.zero,new Vector2(390,52),
            ShowPauseMainPage,Hex("172735"),text);
        SetRect(back.GetComponent<RectTransform>(),0,-427,390,52,new Vector2(.5f,1));

        RefreshDisplaySettingsLabels();
        pauseSettingsPanel.SetActive(false);
        pauseOverlay.SetActive(false);
    }

    void ShowSettingsPage()
    {
        InitDisplaySettings();
        RefreshDisplaySettingsLabels();
        if (pauseStatusText != null) pauseStatusText.text = "";
        if (pauseMainPanel != null) pauseMainPanel.SetActive(false);
        if (pauseSettingsPanel != null) pauseSettingsPanel.SetActive(true);
    }

    void ShowPauseMainPage()
    {
        if (pauseSettingsPanel != null) pauseSettingsPanel.SetActive(false);
        if (pauseMainPanel != null) pauseMainPanel.SetActive(true);
    }

    string DisplayModeLabel()
    {
        return displayModeIndex == 0 ? "ОКОННЫЙ" : "ПОЛНЫЙ ЭКРАН";
    }

    FullScreenMode SelectedFullScreenMode()
    {
        return displayModeIndex == 0
            ? FullScreenMode.Windowed
            : FullScreenMode.FullScreenWindow;
    }

    void RefreshDisplaySettingsLabels()
    {
        if (displayModeText != null)
            displayModeText.text = DisplayModeLabel();

        if (resolutionText != null)
        {
            Vector2Int r = displayResolutions[Mathf.Clamp(
                resolutionIndex,0,displayResolutions.Length-1)];
            resolutionText.text = $"{r.x} × {r.y}";
        }
    }

    void CycleDisplayMode()
    {
        displayModeIndex = (displayModeIndex + 1) % 2;
        RefreshDisplaySettingsLabels();
        if (pauseStatusText != null)
            pauseStatusText.text = "Есть неприменённые изменения.";
    }

    void CycleResolution()
    {
        resolutionIndex = (resolutionIndex + 1) % displayResolutions.Length;
        RefreshDisplaySettingsLabels();
        if (pauseStatusText != null)
            pauseStatusText.text = "Есть неприменённые изменения.";
    }

    void ApplyDisplaySettings()
    {
        Vector2Int r = displayResolutions[Mathf.Clamp(
            resolutionIndex,0,displayResolutions.Length-1)];

        Screen.SetResolution(r.x,r.y,SelectedFullScreenMode());

        if (pauseStatusText != null)
            pauseStatusText.text =
                $"ПРИМЕНЕНО: {r.x} × {r.y}  //  {DisplayModeLabel()}";
    }

    void TogglePauseMenu()
    {
        if (pauseMenuOpen) ClosePauseMenu();
        else OpenPauseMenu();
    }

    void OpenPauseMenu()
    {
        if (pauseOverlay == null) return;

        pauseMenuOpen = true;
        shiftStartedBeforePause = shiftStarted;
        shiftStarted = false;
        Time.timeScale = 0f;

        InitDisplaySettings();
        RefreshDisplaySettingsLabels();
        if (pauseStatusText != null) pauseStatusText.text = "";

        ShowPauseMainPage();
        pauseOverlay.SetActive(true);
    }

    void ClosePauseMenu()
    {
        if (!pauseMenuOpen) return;

        pauseMenuOpen = false;
        Time.timeScale = 1f;

        if (pauseOverlay != null)
            pauseOverlay.SetActive(false);

        bool blockedByOverlay =
            (startOverlay != null && startOverlay.activeSelf) ||
            (summaryOverlay != null && summaryOverlay.activeSelf);

        shiftStarted = shiftStartedBeforePause && !blockedByOverlay;
    }

    void QuitGame()
    {
        Save();
        Time.timeScale = 1f;

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void BuildStartOverlay(Transform parent)
    {
        startOverlay = MakePanel(parent,"Стартовый экран",Vector2.zero,Vector2.zero,
            Vector2.zero,Vector2.one,new Color(bg.r,bg.g,bg.b,.985f));
        Stretch(startOverlay.GetComponent<RectTransform>(),0,0,0,0);

        var title = MakeText(startOverlay.transform,"MOON COURIER CRISIS",46,FontStyle.Bold,text);
        title.alignment = TextAnchor.MiddleCenter;
        SetRect(title.rectTransform,0,-250,900,60,new Vector2(.5f,1));

        var subtitle = MakeText(startOverlay.transform,
            "ЛУННАЯ СЛУЖБА ДОСТАВКИ  //  СМЕНА НА БАЗЕ «АРТЕМИДА»",
            14,FontStyle.Bold,cyan);
        subtitle.alignment = TextAnchor.MiddleCenter;
        SetRect(subtitle.rectTransform,0,-320,800,30,new Vector2(.5f,1));

        var intro = MakePanel(startOverlay.transform,"Инструктаж",new Vector2(0,-385),new Vector2(760,245),
            new Vector2(.5f,1),new Vector2(.5f,1),new Color(panel.r,panel.g,panel.b,.98f));
        intro.GetComponent<RectTransform>().pivot = new Vector2(.5f,1);

        var introText = MakeText(intro.transform,
            "ВАША ЗАДАЧА\n\n" +
            "Выбирайте ровер и заказ, оценивайте вес груза, батарею и риск маршрута.\n" +
            "За 3 лунных дня заработайте как можно больше кредитов и очков.\n\n" +
            "Точки назначения постоянны, но каждый новый день приносит новые заявки.\n" +
            "Невыполненные заявки в конце дня становятся просроченными.\n" +
            "При завершении дня сначала показываются итоги, затем можно перейти дальше.\n" +
            "Повреждённый ровер нужно диагностировать и отремонтировать.\n" +
            "ESC — пауза, настройки экрана и выход из игры.",
            16,FontStyle.Normal,text);
        introText.alignment = TextAnchor.UpperLeft;
        introText.lineSpacing = 1.18f;
        Stretch(introText.rectTransform,30,30,20,24);

        var startButton = MakeButton(startOverlay.transform,"НАЧАТЬ СМЕНУ",Vector2.zero,new Vector2(290,62),
            BeginShift,Hex("0B708E"),text);
        SetRect(startButton.GetComponent<RectTransform>(),0,-665,290,62,new Vector2(.5f,1));
    }

    void BuildSummaryOverlay(Transform parent)
    {
        summaryOverlay = MakePanel(parent,"Итоги дня",Vector2.zero,Vector2.zero,
            Vector2.zero,Vector2.one,new Color(bg.r,bg.g,bg.b,.965f));
        Stretch(summaryOverlay.GetComponent<RectTransform>(),0,0,0,0);

        // Центральная карточка поверх карты.
        var cardPanel = MakePanel(summaryOverlay.transform,"Карточка итогов",
            new Vector2(0,-225),new Vector2(760,500),
            new Vector2(.5f,1),new Vector2(.5f,1),
            new Color(panel.r,panel.g,panel.b,.985f));
        cardPanel.GetComponent<RectTransform>().pivot = new Vector2(.5f,1);

        summaryTitle = MakeText(cardPanel.transform,"ИТОГИ ДНЯ",34,FontStyle.Bold,text);
        summaryTitle.alignment = TextAnchor.MiddleCenter;
        SetRect(summaryTitle.rectTransform,0,-32,680,54,new Vector2(.5f,1));

        var line = MakePanel(cardPanel.transform,"Разделитель",
            new Vector2(0,-100),new Vector2(620,2),
            new Vector2(.5f,1),new Vector2(.5f,1),
            new Color(cyan.r,cyan.g,cyan.b,.70f));
        line.GetComponent<RectTransform>().pivot = new Vector2(.5f,1);

        summaryText = MakeText(cardPanel.transform,"",18,FontStyle.Normal,text);
        summaryText.alignment = TextAnchor.UpperCenter;
        summaryText.lineSpacing = 1.25f;
        SetRect(summaryText.rectTransform,0,-125,650,245,new Vector2(.5f,1));

        summaryActionButton = MakeButton(cardPanel.transform,"ПЕРЕЙТИ К СЛЕДУЮЩЕМУ ДНЮ",
            Vector2.zero,new Vector2(330,58),
            ConfirmDaySummary,Hex("0B708E"),text);
        SetRect(summaryActionButton.GetComponent<RectTransform>(),0,-398,330,58,new Vector2(.5f,1));
        summaryActionText = summaryActionButton.GetComponentInChildren<Text>();

        var hint = MakeText(cardPanel.transform,
            "Невыполненные заявки текущего дня считаются просроченными.",
            11,FontStyle.Normal,muted);
        hint.alignment = TextAnchor.MiddleCenter;
        SetRect(hint.rectTransform,0,-462,620,22,new Vector2(.5f,1));

        summaryOverlay.SetActive(false);
    }

    void ShowStartScreen()
    {
        shiftStarted = false;
        if (startOverlay != null) startOverlay.SetActive(true);
    }

    void BeginShift()
    {
        shiftStarted = true;
        if (startOverlay != null) startOverlay.SetActive(false);
        ShowToast("Смена началась. Выберите ровер и первый заказ.");
    }

    void ShowDaySummary()
    {
        if (summaryOverlay == null || deliveryAnimating) return;

        shiftStarted = false;

        int successes = 0;
        int failures = 0;
        foreach (var d in data.deliveries)
        {
            if (d.day != data.day) continue;
            if (d.success) successes++;
            else failures++;
        }

        int expired = 0;
        foreach (var o in data.orders)
            if (o.status == "Ожидает") expired++;

        int creditDelta = data.credits - data.dayStartCredits;
        int scoreDelta = data.score - data.dayStartScore;

        string creditSign = creditDelta > 0 ? "+" : "";
        string scoreSign = scoreDelta > 0 ? "+" : "";

        if (data.day < 3)
        {
            summaryTitle.text = $"ДЕНЬ {data.day} ЗАВЕРШЁН";

            summaryText.text =
                $"УСПЕШНЫЕ ДОСТАВКИ:  {successes}\n" +
                $"АВАРИИ:  {failures}\n" +
                $"ПРОСРОЧЕНО ЗАЯВОК:  {expired}\n\n" +
                $"КРЕДИТЫ ЗА ДЕНЬ:  {creditSign}{creditDelta}\n" +
                $"ОЧКИ ЗА ДЕНЬ:  {scoreSign}{scoreDelta}\n\n" +
                $"ТЕКУЩИЙ БАЛАНС:  {data.credits} кр.   //   {data.score} очков";

            if (summaryActionText != null)
                summaryActionText.text = $"ПЕРЕЙТИ К ДНЮ {data.day + 1}";
        }
        else
        {
            int allSuccesses = 0;
            int allFailures = 0;
            foreach (var d in data.deliveries)
            {
                if (d.success) allSuccesses++;
                else allFailures++;
            }

            string rating =
                data.credits >= 2200 ? "S  //  ЛЕГЕНДА ЛУННОЙ ЛОГИСТИКИ" :
                data.credits >= 1600 ? "A  //  ОТЛИЧНАЯ СМЕНА" :
                data.credits >= 1000 ? "B  //  БАЗА ОБЕСПЕЧЕНА" :
                                       "C  //  СМЕНА ЗАВЕРШЕНА";

            summaryTitle.text = "ТРИ ЛУННЫХ ДНЯ ЗАВЕРШЕНЫ";

            summaryText.text =
                $"ДЕНЬ 3:  {successes} доставок   //   {failures} аварий   //   {expired} просрочено\n\n" +
                $"ВСЕГО УСПЕШНЫХ ДОСТАВОК:  {allSuccesses}\n" +
                $"ВСЕГО АВАРИЙ:  {allFailures}\n" +
                $"КРЕДИТЫ:  {data.credits}\n" +
                $"ОЧКИ:  {data.score}\n\n" +
                $"РЕЙТИНГ:  {rating}";

            if (summaryActionText != null)
                summaryActionText.text = "НОВАЯ СМЕНА";
        }

        summaryOverlay.SetActive(true);
    }

    void ConfirmDaySummary()
    {
        if (data.day >= 3)
        {
            int expiredFinal = 0;
            foreach (var o in data.orders)
                if (o.status == "Ожидает") expiredFinal++;

            AddEvent($"СМЕНА ЗАВЕРШЕНА // день 3 // просрочено: {expiredFinal} // кредиты: {data.credits} // очки: {data.score}");
            Save();
            RestartFromSummary();
            return;
        }

        AdvanceToNextDay();
    }

    void AdvanceToNextDay()
    {
        int expired = 0;
        foreach (var o in data.orders)
            if (o.status == "Ожидает") expired++;

        AddEvent($"ДЕНЬ {data.day} ЗАВЕРШЁН // просрочено заявок: {expired}");

        data.day++;
        data.deliveriesToday = 0;
        selectedOrder = null;

        // Ночная подзарядка. Повреждение само не исчезает.
        foreach (var r in data.rovers)
        {
            r.battery = Mathf.Min(100f, r.battery + 65f);
            if (r.status == "В пути") r.status = "Готов";
        }

        PopulateOrdersForDay(data.day);

        // Новая точка отсчёта нужна для итогов следующего дня.
        data.dayStartCredits = data.credits;
        data.dayStartScore = data.score;

        AddEvent($"ДЕНЬ {data.day} // получены новые заявки в постоянных точках");
        AddEvent($"БРИФИНГ // {DayBrief(data.day)}");

        Save();

        SpawnGameObjects();
        UpdateSelections();
        RefreshUI();

        if (summaryOverlay != null) summaryOverlay.SetActive(false);
        shiftStarted = true;

        ShowToast($"День {data.day}: получены новые заявки.");
    }

    void RestartFromSummary()
    {
        Time.timeScale = 1f;
        pauseMenuOpen = false;
        if (pauseOverlay != null) pauseOverlay.SetActive(false);
        if (summaryOverlay != null) summaryOverlay.SetActive(false);
        NewGame();
        shiftStarted = true;
        ShowToast("Новая смена началась.");
    }

    void ShowToast(string message)
    {
        if (toastPanel == null || toastText == null) return;
        toastText.text = message;
        toastPanel.SetActive(true);
        toastTimer = 3.4f;
    }

    void CreateTopStat(Transform parent, string label, out Text value, float x)
    {
        var l = MakeText(parent, label, 10, FontStyle.Bold, muted);
        SetRect(l.rectTransform, x, -12, 110, 16, new Vector2(0,1));
        value = MakeText(parent, "--", 19, FontStyle.Bold, text);
        SetRect(value.rectTransform, x, -30, 110, 28, new Vector2(0,1));
    }

    void LoadOrCreate()
    {
        string path = SavePath();
        if (File.Exists(path))
        {
            try
            {
                data = JsonUtility.FromJson<GameSave>(File.ReadAllText(path));
                if (data != null && data.rovers != null && data.rovers.Count > 0)
                {
                    NormalizeLoadedData();
                    return;
                }
            }
            catch { }
        }
        CreateFreshData();
    }

    void NormalizeLoadedData()
    {
        if (data.orders == null) data.orders = new List<OrderData>();
        if (data.orders.Count == 0 && data.day >= 1 && data.day <= 3)
            PopulateOrdersForDay(data.day);
        if (data.deliveries == null) data.deliveries = new List<DeliveryData>();
        if (data.events == null) data.events = new List<string>();

        // Для старого/неполного сохранения не допускаем пустую точку отсчёта дня.
        if (data.dayStartCredits == 0 && data.day == 1 && data.credits > 0)
            data.dayStartCredits = 120;

        foreach (var r in data.rovers)
        {
            if (string.IsNullOrEmpty(r.status)) r.status = "Готов";
            if (r.status == "Требуется ремонт" && string.IsNullOrEmpty(r.damage)) r.damage = "Неизвестная неисправность";
        }
    }

    void CreateFreshData()
    {
        data = new GameSave
        {
            credits = 120,
            score = 0,
            day = 1,
            deliveriesToday = 0,
            dayStartCredits = 120,
            dayStartScore = 0
        };
        data.rovers.Add(NewRover("R1", "ОРИОН", 100, 35, new Vector3(-4.0f,.5f,-3.2f)));
        data.rovers.Add(NewRover("R2", "ЗЕНИТ", 78, 55, new Vector3(0,.5f,-3.2f)));
        data.rovers.Add(NewRover("R3", "ВЕКТОР", 48, 70, new Vector3(4.0f,.5f,-3.2f)));

        PopulateOrdersForDay(1);

        data.events.Add("ДЕНЬ 1 // получены первые заявки // впереди 3 лунных дня // заработайте максимум кредитов и очков.");
        data.events.Add("ТОЧКИ ПОСТОЯННЫ // геологи, обсерватория, медпост, буровая и антенна остаются на своих местах; меняются только заявки.");
        Save();
    }

    RoverData NewRover(string id, string name, float battery, float capacity, Vector3 pos)
    {
        return new RoverData { id=id, roverName=name, battery=battery, capacity=capacity, status="Готов", position=pos };
    }

    OrderData NewOrder(string id, string title, float weight, int reward, int urgency, float risk, string zone, Vector3 pos)
    {
        return new OrderData { id=id, title=title, weight=weight, reward=reward, urgency=urgency, risk=risk, zone=zone, status="Ожидает", position=pos };
    }

    void PopulateOrdersForDay(int day)
    {
        data.orders.Clear();

        // ПЯТЬ ПОСТОЯННЫХ ТОЧЕК НА КАРТЕ:
        Vector3 GEO   = new Vector3(-16,0f, 9);   // геологический лагерь
        Vector3 OBS   = new Vector3( -7,0f,14);   // обсерватория
        Vector3 MED   = new Vector3(  4,0f,15);   // медицинский пост
        Vector3 DRILL = new Vector3( 15,0f,11);   // буровая
        Vector3 ANT   = new Vector3( 12,0f, 1);   // антенна / ретранслятор

        // Риск привязан к местности и точке назначения.
        // Он не "растёт по дням" искусственно.
        const float GEO_RISK   = .20f;
        const float OBS_RISK   = .28f;
        const float MED_RISK   = .52f;
        const float DRILL_RISK = .42f;
        const float ANT_RISK   = .22f;

        if (day <= 1)
        {
            // День 1 сразу показывает весь диапазон механик: 20–52%.
            data.orders.Add(NewOrder("D1-GEO",   "Пайки для геологов",
                18, 140, 3, GEO_RISK, "SAFE", GEO));

            data.orders.Add(NewOrder("D1-OBS",   "Вода для обсерватории",
                42, 290, 2, OBS_RISK, "ROUGH", OBS));

            data.orders.Add(NewOrder("D1-MED",   "Срочный медицинский груз",
                27, 430, 1, MED_RISK, "CRATER", MED));

            data.orders.Add(NewOrder("D1-DRILL", "Расходники для буровой установки",
                45, 390, 2, DRILL_RISK, "CRATER", DRILL));
        }
        else if (day == 2)
        {
            // День стратегического выбора.
            data.orders.Add(NewOrder("D2-GEO", "Кислородные баллоны для геологов",
                30, 220, 2, GEO_RISK, "SAFE", GEO));

            data.orders.Add(NewOrder("D2-OBS", "Аккумуляторы для обсерватории",
                38, 330, 2, OBS_RISK, "ROUGH", OBS));

            // Обе заявки тяжелее лимита ЗЕНИТА (55 кг), поэтому их может
            // взять только ВЕКТОР (70 кг).
            data.orders.Add(NewOrder("D2-MED-HEAVY", "Криоконтейнер для медпоста",
                60, 560, 1, MED_RISK, "CRATER", MED));

            data.orders.Add(NewOrder("D2-DRILL-HEAVY", "Энергомодуль буровой установки",
                68, 700, 1, DRILL_RISK, "CRATER", DRILL));
        }
        else
        {
            // Финальный день: больше тяжёлых и дорогих заявок.
            data.orders.Add(NewOrder("D3-GEO", "Контейнеры для лунных образцов",
                34, 310, 2, GEO_RISK, "SAFE", GEO));

            data.orders.Add(NewOrder("D3-OBS", "Оптический модуль обсерватории",
                50, 480, 1, OBS_RISK, "ROUGH", OBS));

            data.orders.Add(NewOrder("D3-MED", "Плазма и комплект медикаментов",
                46, 660, 1, MED_RISK, "CRATER", MED));

            data.orders.Add(NewOrder("D3-DRILL", "Аварийный энергоблок буровой",
                68, 730, 1, DRILL_RISK, "CRATER", DRILL));

            data.orders.Add(NewOrder("D3-ANT", "Радиомодуль ретранслятора",
                40, 420, 2, ANT_RISK, "ROUGH", ANT));
        }
    }

    string DayBrief(int day)
    {
        if (day == 1)
            return "4 заявки // от безопасных 20% до опасных 52% // все рейсы по отдельности выполнимы";
        if (day == 2)
            return "4 новые заявки // две тяжёлые доступны только ВЕКТОРУ // заряда хватит только на одну из них";
        return "5 финальных заявок // максимальные награды // тяжёлые грузы требуют грамотного распределения роверов";
    }

    void SpawnGameObjects()
    {
        foreach (var old in roverObjects.Values) if (old != null) Destroy(old);
        foreach (var old in orderObjects.Values) if (old != null) Destroy(old);
        roverObjects.Clear();
        orderObjects.Clear();

        foreach (var r in data.rovers)
        {
            var go = BuildRoverModel(r);
            roverObjects[r.id] = go;
        }

        foreach (var o in data.orders)
        {
            if (o.status == "Доставлен") continue;
            var go = BuildOrderBeacon(o);
            orderObjects[o.id] = go;
        }
    }

    GameObject BuildRoverModel(RoverData r)
    {
        var root = new GameObject("Rover_" + r.id + "_" + r.roverName);
        root.transform.position = new Vector3(r.position.x, TerrainHeightAt(r.position.x,r.position.z) + RoverGroundOffset, r.position.z);
        var collider = root.AddComponent<BoxCollider>();
        collider.size = new Vector3(3.0f,1.65f,3.7f);
        collider.center = new Vector3(0,.78f,0);
        var select = root.AddComponent<MapSelectable>();
        select.id = r.id;
        select.isRover = true;

        Color bodyColor = r.status == "Готов" ? Hex("D6DEE2") : Hex("914E58");
        Color roverAccent =
            r.id == "R1" ? Hex("2A9FD6") :
            r.id == "R2" ? Hex("E2A83B") :
                           Hex("38BFA1");

        Primitive(PrimitiveType.Cube, root.transform, "Chassis",
            new Vector3(0,.62f,0), new Vector3(2.1f,.48f,2.65f), bodyColor);
        Primitive(PrimitiveType.Cube, root.transform, "FrontCab",
            new Vector3(0,.95f,-.72f), new Vector3(1.65f,.58f,.88f), Hex("AEBCC3"));

        Primitive(PrimitiveType.Cube, root.transform, "Accent",
            new Vector3(0,.86f,-1.18f), new Vector3(1.22f,.12f,.10f), roverAccent);

        // Солнечная панель из трёх секций.
        for (int i=-1;i<=1;i++)
            Primitive(PrimitiveType.Cube,root.transform,"SolarCell",
                new Vector3(i*.76f,1.28f,.20f),new Vector3(.68f,.07f,1.45f),Hex("123D5B"));

        // Пустая грузовая площадка. Пока игрок НЕ запустил рейс,
        // на ровере нет никакого контейнера.
        Primitive(PrimitiveType.Cube, root.transform, "Empty Cargo Deck",
            new Vector3(0,1.05f,1.02f),new Vector3(1.48f,.12f,.92f),Hex("354149"));

        for (int side=-1; side<=1; side+=2)
        {
            Primitive(PrimitiveType.Cube, root.transform, "Cargo Rail",
                new Vector3(side*.66f,1.18f,1.02f),
                new Vector3(.08f,.18f,.98f),Hex("68757D"));
        }

        Primitive(PrimitiveType.Cube, root.transform, "Cargo Stop",
            new Vector3(0,1.18f,.58f),
            new Vector3(1.38f,.18f,.08f),Hex("68757D"));

        Primitive(PrimitiveType.Cylinder,root.transform,"Sensor Mast",
            new Vector3(0,1.75f,-.62f),new Vector3(.10f,.50f,.10f),Hex("AAB8C0"));
        Primitive(PrimitiveType.Sphere,root.transform,"Sensor Head",
            new Vector3(0,2.18f,-.62f),new Vector3(.36f,.25f,.36f),roverAccent);

        // Фары.
        for (int side=-1; side<=1; side+=2)
        {
            var lamp = Primitive(PrimitiveType.Sphere,root.transform,"Headlight",
                new Vector3(side*.62f,.82f,-1.38f),new Vector3(.16f,.12f,.10f),Hex("E8FBFF"));
            var l = lamp.AddComponent<Light>();
            l.type = LightType.Point; l.range = 3.2f; l.intensity = 1.2f; l.color = new Color(.72f,.92f,1f);
        }

        // Шесть колёс + ступицы.
        for (int side=-1; side<=1; side+=2)
        {
            for (int axle=-1; axle<=1; axle++)
            {
                float z = axle * 1.05f;
                var wheel = Primitive(PrimitiveType.Cylinder,root.transform,"Wheel",
                    new Vector3(side*1.22f,.42f,z),new Vector3(.48f,.24f,.48f),Hex("171D22"));
                wheel.transform.localRotation = Quaternion.Euler(0,0,90);
                var hub = Primitive(PrimitiveType.Cylinder,root.transform,"Hub",
                    new Vector3(side*1.24f,.42f,z),new Vector3(.22f,.26f,.22f),Hex("65717A"));
                hub.transform.localRotation = Quaternion.Euler(0,0,90);
            }
        }


        var ring = Primitive(PrimitiveType.Cylinder, root.transform, "Selection Ring",
            new Vector3(0,.08f,0), new Vector3(1.8f,.018f,1.8f), new Color(cyan.r,cyan.g,cyan.b,.82f));
        ring.name = "SelectionRing";
        ring.SetActive(selectedRover != null && selectedRover.id == r.id);

        CreateWorldLabel(root.transform, r.roverName, new Vector3(0,2.85f,0), roverAccent, 48);

        // Если сохранение загрузилось уже с повреждённым ровером,
        // дым должен быть виден сразу.
        bool alreadyDamaged =
            r.status == "Нужен осмотр" ||
            r.status == "Требуется ремонт";

        SetPersistentDamageSmoke(
            root,
            alreadyDamaged);

        return root;
    }

    GameObject CreateTerrainSelectionRing(Transform parent, Color color)
    {
        var go = new GameObject("SelectionRing");
        go.transform.SetParent(parent,false);

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.positionCount = 56;
        lr.startWidth = .105f;
        lr.endWidth = .105f;
        lr.numCornerVertices = 3;
        lr.numCapVertices = 2;
        lr.alignment = LineAlignment.View;

        var mat = NewLineMaterial(new Color(color.r,color.g,color.b,1f));
        if (mat != null) lr.material = mat;
        lr.startColor = lr.endColor = new Color(color.r,color.g,color.b,1f);

        UpdateTerrainSelectionRing(lr,parent.position,1.55f);
        return go;
    }

    void UpdateTerrainSelectionRing(LineRenderer lr, Vector3 center, float radius)
    {
        if (lr == null) return;

        int count = Mathf.Max(24,lr.positionCount);
        if (lr.positionCount != count) lr.positionCount = count;

        for (int i=0; i<count; i++)
        {
            float angle = (Mathf.PI * 2f * i) / count;
            float x = center.x + Mathf.Cos(angle) * radius;
            float z = center.z + Mathf.Sin(angle) * radius;

            // Каждая точка кольца лежит над СВОЕЙ точкой рельефа.
            // Небольшой оффсет предотвращает z-fighting с грунтом.
            float y = TerrainHeightAt(x,z) + .085f;
            lr.SetPosition(i,new Vector3(x,y,z));
        }
    }

    GameObject BuildOrderBeacon(OrderData o)
    {
        var root = new GameObject("Order_" + o.id);
        float groundY = TerrainHeightAt(o.position.x,o.position.z);
        root.transform.position = new Vector3(o.position.x, groundY + .015f, o.position.z);
        root.transform.rotation = Quaternion.identity;

        var collider = root.AddComponent<CapsuleCollider>();
        collider.radius = .82f;
        collider.height = 1.65f;
        collider.center = new Vector3(0,.72f,0);

        var select = root.AddComponent<MapSelectable>();
        select.id = o.id;
        select.isRover = false;

        Color c = o.risk <= .20f ? green : o.risk < .4f ? amber : red;

        // Низкое основание стоит прямо на грунте.
        Primitive(PrimitiveType.Cylinder,root.transform,"Основание маяка",
            new Vector3(0,.055f,0),new Vector3(.86f,.055f,.86f),Hex("19232A"));

        // Груз заказа — отдельный объект.
        var cargoVisual = new GameObject("CargoVisual");
        cargoVisual.transform.SetParent(root.transform,false);

        Primitive(PrimitiveType.Cube,cargoVisual.transform,"Контейнер",
            new Vector3(0,.31f,0),new Vector3(1.05f,.48f,.82f),Hex("D0D6D8"));
        Primitive(PrimitiveType.Cube,cargoVisual.transform,"Крышка",
            new Vector3(0,.58f,0),new Vector3(1.10f,.07f,.87f),Hex("EEF1F2"));
        Primitive(PrimitiveType.Cube,cargoVisual.transform,"Маркировка",
            new Vector3(0,.31f,-.425f),new Vector3(.66f,.13f,.025f),c);

        // Маяк физически закреплён НА КОНТЕЙНЕРЕ.
        // Поэтому при заборе заказа он уезжает вместе с грузом,
        // а не остаётся висеть в воздухе.
        Primitive(PrimitiveType.Cylinder,cargoVisual.transform,"Мачта",
            new Vector3(0,.86f,0),new Vector3(.06f,.30f,.06f),Hex("B8C5CB"));
        var orb = Primitive(PrimitiveType.Sphere,cargoVisual.transform,"Signal",
            new Vector3(0,1.20f,0),new Vector3(.34f,.34f,.34f),c);

        var light = orb.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 5.3f;
        light.intensity = 2.0f;
        light.color = c;

        // Маркер выбора повторяет настоящий рельеф Луны и не проваливается в кратеры.
        var ring = CreateTerrainSelectionRing(root.transform,c);
        ring.SetActive(selectedOrder != null && selectedOrder.id == o.id);

        var selectedLamp = new GameObject("SelectionGlow");
        selectedLamp.transform.SetParent(root.transform,false);
        selectedLamp.transform.localPosition = new Vector3(0,1.18f,0);
        var selectedLight = selectedLamp.AddComponent<Light>();
        selectedLight.type = LightType.Point;
        selectedLight.range = 11.0f;
        selectedLight.intensity = 7.0f;
        selectedLight.color = c;
        selectedLamp.SetActive(selectedOrder != null && selectedOrder.id == o.id);

        CreateWorldLabel(root.transform,
            o.title.ToUpperInvariant() + "\n" + $"{o.reward} КР.  •  {o.weight:0} КГ",
            new Vector3(0,2.12f,0), c, 35);

        return root;
    }

    public void Pick(string id, bool rover)
    {
        if (deliveryAnimating) return;
        if (rover) selectedRover = data.rovers.Find(x => x.id == id);
        else selectedOrder = data.orders.Find(x => x.id == id && x.status == "Ожидает");
        UpdateSelections();
        RefreshUI();
    }

    void CycleRover()
    {
        if (deliveryAnimating || data.rovers.Count == 0) return;
        int idx = selectedRover == null ? -1 : data.rovers.IndexOf(selectedRover);
        selectedRover = data.rovers[(idx + 1) % data.rovers.Count];
        UpdateSelections();
        RefreshUI();
    }

    void CycleOrder()
    {
        if (deliveryAnimating) return;
        var available = data.orders.FindAll(x => x.status == "Ожидает");
        if (available.Count == 0) return;
        int idx = selectedOrder == null ? -1 : available.IndexOf(selectedOrder);
        selectedOrder = available[(idx + 1) % available.Count];
        UpdateSelections();
        RefreshUI();
    }

    void UpdateSelections()
    {
        foreach (var kv in roverObjects)
        {
            var ring = kv.Value.transform.Find("SelectionRing");
            if (ring != null) ring.gameObject.SetActive(selectedRover != null && selectedRover.id == kv.Key);
        }
        foreach (var kv in orderObjects)
        {
            bool active = selectedOrder != null && selectedOrder.id == kv.Key;

            var ring = kv.Value.transform.Find("SelectionRing");
            if (ring != null) ring.gameObject.SetActive(active);

            var glow = kv.Value.transform.Find("SelectionGlow");
            if (glow != null) glow.gameObject.SetActive(active);
        }
        RefreshRouteLine();
    }

    void RefreshRouteLine()
    {
        // Маршрут рассчитывается скрыто. Игрок видит только подсветку выбранного заказа
        // и результат расчёта в правой карточке.
        if (routeLine != null)
            routeLine.positionCount = 0;
    }

    List<Vector3> BuildSafeRoute(Vector3 start, Vector3 end, string movingRoverId)
    {
        start.y = TerrainHeightAt(start.x,start.z) + RoverGroundOffset;
        end.y = TerrainHeightAt(end.x,end.z) + RoverGroundOffset;

        // У каждого ровера есть собственная прямая парковочная полоса.
        // ОРИОН, ЗЕНИТ и ВЕКТОР сначала полностью выезжают вперёд из ряда
        // на своём X и только после этого начинают поворачивать на маршрут.
        //
        // Обратный путь строится через Reverse(), поэтому на возвращении
        // ровер сначала приходит на выход СВОЕЙ полосы, а затем заезжает
        // к парковочному месту строго по прямой, не пересекая соседей.
        bool isParkingRover =
            movingRoverId == "R1" ||
            movingRoverId == "R2" ||
            movingRoverId == "R3";

        if (isParkingRover && start.z < -1.40f)
        {
            const float parkingLaneExitZ = .45f;

            Vector3 laneExit = new Vector3(
                start.x,
                TerrainHeightAt(start.x,parkingLaneExitZ) + RoverGroundOffset,
                parkingLaneExitZ
            );

            List<Vector3> rest =
                BuildSafeRoute(laneExit,end,movingRoverId);

            if (rest == null || rest.Count == 0)
                return null;

            var result = new List<Vector3>();
            result.Add(start);
            result.Add(laneExit);

            for (int i=1; i<rest.Count; i++)
                result.Add(rest[i]);

            return result;
        }

        // Если физического препятствия нет — едем прямо.
        // Никакие невидимые границы зон не влияют на траекторию.
        if (SegmentClear(start,end,movingRoverId,start,end))
            return new List<Vector3> { start, end };

        Vector2Int startCell = WorldToCell(start);
        Vector2Int endCell = WorldToCell(end);

        var open = new List<Vector2Int>();
        var closed = new HashSet<Vector2Int>();
        var cameFrom = new Dictionary<Vector2Int, Vector2Int>();
        var gScore = new Dictionary<Vector2Int, float>();

        open.Add(startCell);
        gScore[startCell] = 0f;

        int[,] dirs =
        {
            { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 },
            { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 }
        };

        int guard = 0;
        while (open.Count > 0 && guard++ < 6000)
        {
            int bestIndex = 0;
            float bestF = float.MaxValue;
            for (int i = 0; i < open.Count; i++)
            {
                Vector2Int c = open[i];
                float g = gScore.ContainsKey(c) ? gScore[c] : float.MaxValue;
                float h = Vector2Int.Distance(c,endCell);
                float f = g + h;
                if (f < bestF)
                {
                    bestF = f;
                    bestIndex = i;
                }
            }

            Vector2Int current = open[bestIndex];
            open.RemoveAt(bestIndex);

            if (current == endCell)
            {
                List<Vector3> raw = ReconstructPath(cameFrom,current,start,end);
                return SimplifyPath(raw,movingRoverId,start,end);
            }

            if (closed.Contains(current)) continue;
            closed.Add(current);

            for (int d = 0; d < 8; d++)
            {
                Vector2Int next = new Vector2Int(current.x + dirs[d,0], current.y + dirs[d,1]);
                Vector3 wp = CellToWorld(next);
                if (wp.x < PathMinX || wp.x > PathMaxX || wp.z < PathMinZ || wp.z > PathMaxZ) continue;
                if (next != endCell && next != startCell && IsPathBlocked(wp,movingRoverId,start,end)) continue;
                if (closed.Contains(next)) continue;

                bool diagonal = dirs[d,0] != 0 && dirs[d,1] != 0;
                float stepDistance = diagonal ? 1.4142f : 1f;

                Vector3 currentWp = CellToWorld(current);
                float terrainMult = TerrainRoutePlanningCostAt(wp.x,wp.z);
                float tentative = gScore[current] + stepDistance * terrainMult;

                if (!gScore.ContainsKey(next) || tentative < gScore[next])
                {
                    cameFrom[next] = current;
                    gScore[next] = tentative;
                    if (!open.Contains(next)) open.Add(next);
                }
            }
        }

        return new List<Vector3>();
    }

    Vector2Int WorldToCell(Vector3 p)
    {
        int x = Mathf.RoundToInt((p.x - PathMinX) / PathStep);
        int z = Mathf.RoundToInt((p.z - PathMinZ) / PathStep);
        return new Vector2Int(x,z);
    }

    Vector3 CellToWorld(Vector2Int c)
    {
        float x = PathMinX + c.x * PathStep;
        float z = PathMinZ + c.y * PathStep;
        return new Vector3(x, TerrainHeightAt(x,z) + RoverGroundOffset, z);
    }

    List<Vector3> ReconstructPath(Dictionary<Vector2Int,Vector2Int> cameFrom, Vector2Int current, Vector3 exactStart, Vector3 exactEnd)
    {
        var cells = new List<Vector2Int>();
        cells.Add(current);
        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            cells.Add(current);
        }
        cells.Reverse();

        var path = new List<Vector3>();
        path.Add(exactStart);
        for (int i = 1; i < cells.Count - 1; i++)
            path.Add(CellToWorld(cells[i]));
        path.Add(exactEnd);
        return path;
    }

    List<Vector3> SimplifyPath(List<Vector3> raw, string movingRoverId, Vector3 start, Vector3 end)
    {
        if (raw == null || raw.Count <= 2) return raw;
        var result = new List<Vector3>();
        result.Add(raw[0]);

        int anchor = 0;
        while (anchor < raw.Count - 1)
        {
            int best = anchor + 1;
            for (int candidate = raw.Count - 1; candidate > anchor + 1; candidate--)
            {
                if (SegmentClear(raw[anchor],raw[candidate],movingRoverId,start,end))
                {
                    best = candidate;
                    break;
                }
            }
            result.Add(raw[best]);
            anchor = best;
        }
        return result;
    }

    bool IsPathBlocked(Vector3 p, string movingRoverId, Vector3 start, Vector3 end)
    {
        Vector2 point = new Vector2(p.x,p.z);
        if (Vector2.Distance(point,new Vector2(start.x,start.z)) < .9f) return false;
        if (Vector2.Distance(point,new Vector2(end.x,end.z)) < .9f) return false;

        // Блокируем только реальные зарегистрированные препятствия.
        const float roverClearance = .58f;

        for (int i = 0; i < routeObstacleCenters.Count; i++)
            if (Vector2.Distance(point,routeObstacleCenters[i]) < routeObstacleRadii[i] + roverClearance)
                return true;

        foreach (RoverData r in data.rovers)
        {
            if (r.id == movingRoverId) continue;
            Vector2 rp = new Vector2(r.position.x,r.position.z);
            if (Vector2.Distance(point,rp) < 1.48f)
                return true;
        }

        return false;
    }

    bool SegmentStaysInTerrainKind(Vector3 a, Vector3 b, TerrainKind kind)
    {
        float distance = Vector3.Distance(a,b);
        int samples = Mathf.Max(2,Mathf.CeilToInt(distance / .65f));
        for (int i = 1; i < samples; i++)
        {
            float t = i / (float)samples;
            Vector3 p = Vector3.Lerp(a,b,t);
            if (TerrainKindAt(p.x,p.z) != kind) return false;
        }
        return true;
    }

    bool SegmentClear(Vector3 a, Vector3 b, string movingRoverId, Vector3 start, Vector3 end)
    {
        Vector2 aa = new Vector2(a.x,a.z);
        Vector2 bb = new Vector2(b.x,b.z);
        const float roverClearance = .58f;

        for (int i = 0; i < routeObstacleCenters.Count; i++)
        {
            if (DistancePointToSegment(routeObstacleCenters[i],aa,bb) < routeObstacleRadii[i] + roverClearance)
                return false;
        }

        foreach (RoverData r in data.rovers)
        {
            if (r.id == movingRoverId) continue;
            Vector2 rp = new Vector2(r.position.x,r.position.z);
            if (DistancePointToSegment(rp,aa,bb) < 1.38f)
                return false;
        }
        return true;
    }

    float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b-a;
        float sqr = ab.sqrMagnitude;
        if (sqr < .0001f) return Vector2.Distance(p,a);
        float t = Mathf.Clamp01(Vector2.Dot(p-a,ab) / sqr);
        return Vector2.Distance(p,a + ab*t);
    }

    float PathLength(List<Vector3> path)
    {
        if (path == null || path.Count < 2) return 0f;
        float length = 0f;
        for (int i = 1; i < path.Count; i++)
            length += Vector3.Distance(path[i-1],path[i]);
        return length;
    }

    float PathTerrainWeightedLength(List<Vector3> path)
    {
        if (path == null || path.Count < 2) return 0f;
        float total = 0f;

        for (int i = 1; i < path.Count; i++)
        {
            Vector3 a = path[i-1];
            Vector3 b = path[i];
            float distance = Vector3.Distance(a,b);
            int samples = Mathf.Max(1,Mathf.CeilToInt(distance / .65f));
            float segment = 0f;

            for (int n = 0; n < samples; n++)
            {
                float t = (n + .5f) / samples;
                Vector3 p = Vector3.Lerp(a,b,t);
                segment += TerrainTraversalMultiplierAt(p.x,p.z);
            }

            total += distance * (segment / samples);
        }
        return total;
    }

    Vector3 DeliveryStopPoint(RoverData r, OrderData o)
    {
        Vector3 from = new Vector3(r.position.x,0,r.position.z);
        Vector3 to = new Vector3(o.position.x,0,o.position.z);
        Vector3 dir = from - to;
        if (dir.sqrMagnitude < .01f) dir = Vector3.back;
        dir.Normalize();
        Vector3 stop = to + dir * 1.75f;
        stop.y = TerrainHeightAt(stop.x,stop.z) + RoverGroundOffset;
        return stop;
    }

    void StartDelivery()
    {
        if (deliveryAnimating || selectedRover == null || selectedOrder == null) return;
        var calc = CalculateRoute(selectedRover, selectedOrder);
        if (!calc.canLaunch)
        {
            AddEvent(calc.reason);
            return;
        }
        StartCoroutine(DeliveryRoutine(calc.batteryNeeded, calc.path));
    }

    IEnumerator DeliveryRoutine(float needed, List<Vector3> path)
    {
        deliveryAnimating = true;
        launchButton.interactable = false;
        launchText.text = "РОВЕР В ПУТИ...";

        RoverData rover = selectedRover;
        OrderData order = selectedOrder;
        rover.status = "В пути";
        rover.battery = Mathf.Max(0, rover.battery - needed);
        RefreshUI();

        GameObject roverGo = roverObjects[rover.id];
        Vector3 home = roverGo.transform.position;
        Quaternion homeRotation = roverGo.transform.rotation;

        // С базы ровер всегда едет пустым.
        yield return StartCoroutine(MoveRoverAlongPath(roverGo,path,5.6f));
        yield return new WaitForSeconds(.12f);

        GameObject orderGo = orderObjects.ContainsKey(order.id)
            ? orderObjects[order.id]
            : null;

        // Событие и риск определяются уже в точке заказа.
        RouteRandomEvent randomEvent = RollRouteRandomEvent();

        if (!string.IsNullOrEmpty(randomEvent.title))
        {
            rover.battery = Mathf.Clamp(
                rover.battery + randomEvent.batteryDelta,0f,100f);
            data.credits = Mathf.Max(
                0,data.credits + randomEvent.creditDelta);
            data.score += randomEvent.scoreDelta;

            string delta = "";
            if (randomEvent.batteryDelta > 0)
                delta += $"  +{randomEvent.batteryDelta:0}% батареи";
            if (randomEvent.batteryDelta < 0)
                delta += $"  {randomEvent.batteryDelta:0}% батареи";
            if (randomEvent.creditDelta > 0)
                delta += $"  +{randomEvent.creditDelta} кр.";
            if (randomEvent.scoreDelta > 0)
                delta += $"  +{randomEvent.scoreDelta} очков";

            AddEvent(
                $"СЛУЧАЙНОЕ СОБЫТИЕ // {randomEvent.title} // {randomEvent.description}{delta}");
        }

        float failureChance =
            Mathf.Clamp01(order.risk + randomEvent.riskDelta);
        bool success =
            UnityEngine.Random.value >= failureChance;

        int payout = 0;
        string result;

        var returnPath = new List<Vector3>(path);
        returnPath.Reverse();

        if (!success)
        {
            // АВАРИЯ ДО ПОГРУЗКИ:
            // контейнер вообще не двигается и остаётся в точке заказа.
            int penalty = Mathf.RoundToInt(order.reward * .15f);

            data.credits =
                Mathf.Max(0,data.credits - penalty);
            data.score -= 75;

            rover.status = "Нужен осмотр";
            rover.inspectionDone = false;
            rover.damage = "";
            rover.repairCost = 0;

            // Дым включается сразу и больше сам не исчезает.
            SetPersistentDamageSmoke(roverGo,true);

            // Очень короткая авария без долгого стояния.
            yield return StartCoroutine(
                PlayQuickAccidentFx(roverGo));

            result =
                $"АВАРИЯ У ТОЧКИ ЗАКАЗА // груз не забран // -{penalty} кр. // {rover.roverName}: требуется диагностика";

            // Ровер сразу возвращается пустым и продолжает дымиться.
            yield return StartCoroutine(
                MoveRoverAlongPath(roverGo,returnPath,5.5f));
        }
        else
        {
            // Только если аварии НЕ было — забираем груз.
            GameObject cargoPackage =
                CreatePickupCargo(orderGo,order);

            // Переносимый контейнер уже создан, поэтому скрываем
            // исходную точку заказа целиком: груз, маяк, подпись и подложку.
            SetOrderPointVisualsVisible(orderGo,false);

            yield return StartCoroutine(
                AnimateCargoPickup(
                    roverGo,cargoPackage,orderGo));

            // На обратном пути ровер реально везёт контейнер.
            StartCoroutine(
                PlayRouteEventFx(roverGo,randomEvent));

            yield return StartCoroutine(
                MoveRoverAlongPath(
                    roverGo,returnPath,6.0f));

            roverGo.transform.position = home;
            roverGo.transform.rotation = homeRotation;

            // Разгрузка только после возвращения на базу.
            if (cargoPackage != null)
            {
                yield return StartCoroutine(
                    AnimateCargoDeliveryAtBase(
                        roverGo,cargoPackage));
            }

            payout = Mathf.RoundToInt(
                order.reward *
                (order.urgency == 1 ? 1.25f : 1f));

            data.credits += payout;
            data.score +=
                payout +
                Mathf.RoundToInt(
                    (1f-order.risk)*100f);

            order.status = "Доставлен";
            rover.status = "Готов";

            result =
                $"ДОСТАВКА ВЫПОЛНЕНА // {order.title} // +{payout} кр. // батарея -{needed:0}%";

            if (orderObjects.ContainsKey(order.id))
            {
                Destroy(orderObjects[order.id]);
                orderObjects.Remove(order.id);
            }
        }

        roverGo.transform.position = home;
        roverGo.transform.rotation = homeRotation;

        if (success)
            data.deliveriesToday++;

        data.deliveries.Add(new DeliveryData
        {
            id="D"+(data.deliveries.Count+1),
            roverId=rover.id,
            orderId=order.id,
            batterySpent=needed,
            success=success,
            reward=payout,
            eventText=result,
            day=data.day
        });

        AddEvent(result);
        Save();

        deliveryAnimating = false;
        UpdateRoverVisual(rover);

        if (order.status == "Доставлен")
            selectedOrder = null;

        UpdateSelections();
        RefreshUI();
    }

    void SetOrderPointVisualsVisible(GameObject orderGo, bool visible)
    {
        if (orderGo == null) return;

        foreach (var renderer in orderGo.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = visible;

        foreach (var canvas in orderGo.GetComponentsInChildren<Canvas>(true))
            canvas.enabled = visible;

        foreach (var light in orderGo.GetComponentsInChildren<Light>(true))
            light.enabled = visible;

        foreach (var collider in orderGo.GetComponentsInChildren<Collider>(true))
            collider.enabled = visible;
    }

    void SetOrderCargoVisible(GameObject orderGo, bool visible)
    {
        if (orderGo == null) return;
        Transform cargo = orderGo.transform.Find("CargoVisual");
        if (cargo != null) cargo.gameObject.SetActive(visible);
    }

    GameObject CreatePickupCargo(GameObject orderGo, OrderData order)
    {
        if (orderGo == null) return null;

        Color cargoColor =
            order.risk >= .4f ? Hex("C98243") :
            order.urgency == 1 ? Hex("D8B24E") :
                                 Hex("B8BFC2");

        var cargo = new GameObject("TransitCargo");
        cargo.transform.position =
            orderGo.transform.TransformPoint(new Vector3(0,.39f,0));
        cargo.transform.rotation = orderGo.transform.rotation;

        Primitive(PrimitiveType.Cube,cargo.transform,"CargoBody",
            Vector3.zero,new Vector3(1.05f,.48f,.82f),cargoColor);
        Primitive(PrimitiveType.Cube,cargo.transform,"CargoLid",
            new Vector3(0,.275f,0),new Vector3(1.10f,.07f,.87f),Hex("EEF1F2"));

        Color marking =
            order.risk <= .20f ? green :
            order.risk < .4f ? amber : red;

        Primitive(PrimitiveType.Cube,cargo.transform,"CargoMark",
            new Vector3(0,0,-.425f),new Vector3(.66f,.13f,.025f),marking);

        for (int side=-1; side<=1; side+=2)
        {
            Primitive(PrimitiveType.Cube,cargo.transform,"CargoCorner",
                new Vector3(side*.49f,0,0),
                new Vector3(.05f,.53f,.86f),Hex("39444A"));
        }

        // Переносимый маяк контейнера.
        Primitive(PrimitiveType.Cylinder,cargo.transform,"TransitMast",
            new Vector3(0,.55f,0),new Vector3(.055f,.26f,.055f),Hex("B8C5CB"));
        var signal = Primitive(PrimitiveType.Sphere,cargo.transform,"TransitSignal",
            new Vector3(0,.84f,0),new Vector3(.28f,.28f,.28f),marking);

        var signalLight = signal.AddComponent<Light>();
        signalLight.type = LightType.Point;
        signalLight.range = 3.6f;
        signalLight.intensity = 1.25f;
        signalLight.color = marking;

        return cargo;
    }

    IEnumerator AnimateCargoPickup(
        GameObject roverGo,
        GameObject cargo,
        GameObject orderGo)
    {
        if (roverGo == null || cargo == null) yield break;

        Vector3 start = cargo.transform.position;
        Quaternion startRot = cargo.transform.rotation;

        Vector3 targetLocal = new Vector3(0,1.50f,1.08f);
        Vector3 targetWorld = roverGo.transform.TransformPoint(targetLocal);
        Quaternion targetRot = roverGo.transform.rotation;

        StartCoroutine(
            PlayPulseLightAtPoint(
                start + Vector3.up*.18f,Hex("63D7F0"),1.8f,.55f));

        float elapsed = 0f;
        const float duration = .78f;

        while (elapsed < duration && cargo != null)
        {
            elapsed += Time.deltaTime;
            float p = Mathf.Clamp01(elapsed/duration);
            float smooth = p*p*(3f-2f*p);

            targetWorld = roverGo.transform.TransformPoint(targetLocal);

            Vector3 pos = Vector3.Lerp(start,targetWorld,smooth);
            pos.y += Mathf.Sin(p*Mathf.PI)*.58f;

            cargo.transform.position = pos;
            cargo.transform.rotation =
                Quaternion.Slerp(startRot,targetRot,smooth);
            yield return null;
        }

        if (cargo != null && roverGo != null)
        {
            cargo.transform.SetParent(roverGo.transform,true);
            cargo.transform.localPosition = targetLocal;
            cargo.transform.localRotation = Quaternion.identity;

            Vector3 lockPos = cargo.transform.position;
            SpawnDirectionalBurst(
                lockPos + roverGo.transform.right*.42f,
                roverGo.transform.up,
                Hex("69DFF4"),6,.42f,.035f,.28f,0f);
            SpawnDirectionalBurst(
                lockPos - roverGo.transform.right*.42f,
                roverGo.transform.up,
                Hex("69DFF4"),6,.42f,.035f,.28f,0f);
        }

        yield return new WaitForSeconds(.14f);
    }

    IEnumerator AnimateCargoDeliveryAtBase(
        GameObject roverGo,
        GameObject cargo)
    {
        if (roverGo == null || cargo == null) yield break;

        cargo.transform.SetParent(null,true);

        Vector3 start = cargo.transform.position;
        Quaternion startRot = cargo.transform.rotation;

        Vector3 end =
            roverGo.transform.position +
            roverGo.transform.right*1.85f -
            roverGo.transform.forward*.35f;
        end.y = TerrainHeightAt(end.x,end.z) + .34f;

        Quaternion endRot =
            Quaternion.Euler(0,roverGo.transform.eulerAngles.y,0);

        StartCoroutine(
            PlayPulseLightFx(roverGo,Hex("72DDB4"),1.8f,.65f));

        float elapsed = 0f;
        const float duration = .78f;

        while (elapsed < duration && cargo != null)
        {
            elapsed += Time.deltaTime;
            float p = Mathf.Clamp01(elapsed/duration);
            float smooth = p*p*(3f-2f*p);

            Vector3 pos = Vector3.Lerp(start,end,smooth);
            pos.y += Mathf.Sin(p*Mathf.PI)*.42f;

            cargo.transform.position = pos;
            cargo.transform.rotation =
                Quaternion.Slerp(startRot,endRot,smooth);
            yield return null;
        }

        if (cargo != null)
        {
            SpawnRadialBurst(
                end + Vector3.up*.22f,
                Hex("79E0B8"),18,.56f,.045f,.45f,-.06f);

            float fade = 0f;
            Vector3 startScale = cargo.transform.localScale;

            while (fade < 1f && cargo != null)
            {
                fade += Time.deltaTime/.34f;
                float p = Mathf.Clamp01(fade);

                cargo.transform.localScale =
                    Vector3.Lerp(
                        startScale,new Vector3(.08f,.08f,.08f),p);
                cargo.transform.position =
                    Vector3.Lerp(end,end + Vector3.up*.42f,p);

                yield return null;
            }

            if (cargo != null) Destroy(cargo);
        }
    }

    IEnumerator PlayQuickAccidentFx(GameObject roverGo)
    {
        if (roverGo == null) yield break;

        Vector3 impact =
            roverGo.transform.position +
            roverGo.transform.right*.68f +
            roverGo.transform.up*.94f;

        StartCoroutine(
            PlayPulseLightAtPoint(
                impact,Hex("FF5949"),4.6f,.42f));

        // Один чёткий удар, а не длинная серия.
        SpawnDirectionalBurst(
            impact,
            roverGo.transform.right + Vector3.up*.46f,
            Hex("FFC15A"),24,2.30f,.045f,.62f,.72f);

        SpawnDirectionalBurst(
            impact,
            roverGo.transform.right + Vector3.up*.20f,
            Hex("F16B50"),10,1.35f,.060f,.56f,.40f);

        Vector3 basePos =
            roverGo.transform.position;
        Quaternion baseRot =
            roverGo.transform.rotation;

        float elapsed = 0f;
        const float duration = .42f;

        while (elapsed < duration && roverGo != null)
        {
            elapsed += Time.deltaTime;

            float p =
                Mathf.Clamp01(elapsed/duration);

            float damp =
                1f-p;

            roverGo.transform.position =
                basePos +
                roverGo.transform.right *
                Mathf.Sin(p*Mathf.PI*4f) *
                .045f *
                damp;

            roverGo.transform.rotation =
                baseRot *
                Quaternion.Euler(
                    0,
                    0,
                    Mathf.Sin(p*Mathf.PI*3f) *
                    1.8f *
                    damp);

            yield return null;
        }

        if (roverGo != null)
        {
            roverGo.transform.position = basePos;
            roverGo.transform.rotation = baseRot;
        }

        // Небольшая пауза после удара — затем сразу обратный путь.
        yield return new WaitForSeconds(.10f);
    }

    IEnumerator PlayRouteEventFx(
        GameObject roverGo,
        RouteRandomEvent routeEvent)
    {
        if (roverGo == null ||
            string.IsNullOrEmpty(routeEvent.title))
            yield break;

        yield return new WaitForSeconds(.32f);

        if (routeEvent.title == "РЫХЛЫЙ РЕГОЛИТ")
            yield return StartCoroutine(
                PlayRegolithDustFx(roverGo));
        else if (routeEvent.title == "СОЛНЕЧНОЕ ОКНО")
            yield return StartCoroutine(
                PlaySolarWindowFx(roverGo));
        else if (routeEvent.title == "АВАРИЙНЫЙ КОНТЕЙНЕР")
            yield return StartCoroutine(
                PlayFoundContainerFx(roverGo));
        else if (routeEvent.title == "РАДИОПОМЕХИ")
            yield return StartCoroutine(
                PlayRadioInterferenceFx(roverGo));
    }

    IEnumerator PlayRegolithDustFx(GameObject roverGo)
    {
        if (roverGo == null) yield break;

        float elapsed = 0f;
        while (elapsed < 1.05f && roverGo != null)
        {
            elapsed += Time.deltaTime;

            Vector3 rear =
                roverGo.transform.position +
                roverGo.transform.forward*1.05f +
                Vector3.up*.16f;

            SpawnDirectionalBurst(
                rear + roverGo.transform.right*.70f,
                -roverGo.transform.forward + Vector3.up*.16f,
                Hex("99958E"),4,.68f,.14f,.56f,.12f);

            SpawnDirectionalBurst(
                rear - roverGo.transform.right*.70f,
                -roverGo.transform.forward + Vector3.up*.16f,
                Hex("99958E"),4,.68f,.14f,.56f,.12f);

            yield return new WaitForSeconds(.10f);
        }
    }

    IEnumerator PlaySolarWindowFx(GameObject roverGo)
    {
        if (roverGo == null) yield break;

        StartCoroutine(
            PlayPulseLightFx(
                roverGo,Hex("FFD86A"),2.15f,1.0f));

        for (int wave=0; wave<3; wave++)
        {
            if (roverGo == null) yield break;

            Vector3 p =
                roverGo.transform.position +
                roverGo.transform.up*1.42f +
                roverGo.transform.forward*.12f;

            SpawnRadialBurst(
                p,Hex("FFE18A"),12,.46f,.043f,.52f,-.08f);

            yield return new WaitForSeconds(.20f);
        }
    }

    IEnumerator PlayFoundContainerFx(GameObject roverGo)
    {
        if (roverGo == null) yield break;

        var marker = new GameObject("FoundContainerFX");
        Vector3 basePos =
            roverGo.transform.position -
            roverGo.transform.right*1.45f +
            roverGo.transform.forward*.20f;

        marker.transform.position =
            new Vector3(
                basePos.x,
                TerrainHeightAt(basePos.x,basePos.z)+.28f,
                basePos.z);

        Primitive(
            PrimitiveType.Cube,
            marker.transform,
            "FoundBox",
            Vector3.zero,
            new Vector3(.48f,.34f,.44f),
            Hex("C9983E"));

        Primitive(
            PrimitiveType.Cube,
            marker.transform,
            "FoundBand",
            new Vector3(0,.18f,0),
            new Vector3(.52f,.035f,.48f),
            Hex("FFE083"));

        StartCoroutine(
            PlayPulseLightAt(
                marker.transform,Hex("E9B94D"),1.9f,.72f));

        Vector3 start = marker.transform.position;
        float elapsed = 0f;

        while (elapsed < .76f && marker != null)
        {
            elapsed += Time.deltaTime;
            float p = Mathf.Clamp01(elapsed/.76f);

            marker.transform.position =
                start + Vector3.up*(p*.68f);

            marker.transform.Rotate(
                Vector3.up,115f*Time.deltaTime,Space.World);

            if (p > .58f)
                marker.transform.localScale =
                    Vector3.Lerp(
                        Vector3.one,
                        Vector3.one*.08f,
                        (p-.58f)/.42f);

            yield return null;
        }

        if (marker != null) Destroy(marker);
    }

    IEnumerator PlayRadioInterferenceFx(GameObject roverGo)
    {
        if (roverGo == null) yield break;

        Transform head =
            roverGo.transform.Find("Sensor Head");

        Vector3 center =
            head != null
            ? head.position
            : roverGo.transform.position + Vector3.up*2.05f;

        for (int pulse=0; pulse<5; pulse++)
        {
            if (roverGo == null) yield break;

            CreateElectricArc(
                center + UnityEngine.Random.insideUnitSphere*.18f,
                center + UnityEngine.Random.onUnitSphere*.68f,
                Hex("67DDF2"));

            SpawnDirectionalBurst(
                center,
                Vector3.up,
                Hex("7DE8F7"),
                5,1.10f,.034f,.29f,-.10f);

            yield return new WaitForSeconds(.11f);
        }
    }

    void SpawnDirectionalBurst(
        Vector3 position,
        Vector3 direction,
        Color color,
        int count,
        float speed,
        float size,
        float lifetime,
        float gravity)
    {
        var fx = new GameObject("DirectionalFX");
        fx.transform.position = position;
        fx.transform.rotation =
            Quaternion.FromToRotation(
                Vector3.forward,
                direction.sqrMagnitude > .001f
                    ? direction.normalized
                    : Vector3.up);

        var ps = fx.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.duration = .12f;
        main.loop = false;
        main.startLifetime = lifetime;
        main.startSpeed =
            new ParticleSystem.MinMaxCurve(speed*.55f,speed);
        main.startSize =
            new ParticleSystem.MinMaxCurve(size*.60f,size*1.25f);
        main.startColor = color;
        main.gravityModifier = gravity;
        main.simulationSpace =
            ParticleSystemSimulationSpace.World;
        main.maxParticles = Mathf.Max(24,count+4);

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[]
        {
            new ParticleSystem.Burst(
                0f,(short)Mathf.Clamp(count,1,120))
        });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 24f;
        shape.radius = .10f;

        var col = ps.colorOverLifetime;
        col.enabled = true;

        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(color,0f),
                new GradientColorKey(color*.72f,1f)
            },
            new[]
            {
                new GradientAlphaKey(1f,0f),
                new GradientAlphaKey(.65f,.55f),
                new GradientAlphaKey(0f,1f)
            });

        col.color = gradient;

        var renderer =
            fx.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode =
            ParticleSystemRenderMode.Billboard;

        var mat = NewLineMaterial(color);
        if (mat != null) renderer.material = mat;

        ps.Play();
        Destroy(fx,lifetime+.55f);
    }

    void SpawnRadialBurst(
        Vector3 position,
        Color color,
        int count,
        float speed,
        float size,
        float lifetime,
        float gravity)
    {
        var fx = new GameObject("RadialFX");
        fx.transform.position = position;

        var ps = fx.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.duration = .10f;
        main.loop = false;
        main.startLifetime = lifetime;
        main.startSpeed =
            new ParticleSystem.MinMaxCurve(speed*.45f,speed);
        main.startSize =
            new ParticleSystem.MinMaxCurve(size*.65f,size*1.20f);
        main.startColor = color;
        main.gravityModifier = gravity;
        main.simulationSpace =
            ParticleSystemSimulationSpace.World;
        main.maxParticles = Mathf.Max(24,count+4);

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[]
        {
            new ParticleSystem.Burst(
                0f,(short)Mathf.Clamp(count,1,120))
        });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = .18f;

        var col = ps.colorOverLifetime;
        col.enabled = true;

        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(color,0f),
                new GradientColorKey(color*.75f,1f)
            },
            new[]
            {
                new GradientAlphaKey(1f,0f),
                new GradientAlphaKey(.45f,.68f),
                new GradientAlphaKey(0f,1f)
            });

        col.color = gradient;

        var renderer =
            fx.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode =
            ParticleSystemRenderMode.Billboard;

        var mat = NewLineMaterial(color);
        if (mat != null) renderer.material = mat;

        ps.Play();
        Destroy(fx,lifetime+.55f);
    }

    void CreateElectricArc(
        Vector3 start,
        Vector3 end,
        Color color)
    {
        var go = new GameObject("RadioArc");
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 7;
        lr.startWidth = .035f;
        lr.endWidth = .012f;
        lr.numCornerVertices = 2;
        lr.startColor = color;
        lr.endColor =
            new Color(color.r,color.g,color.b,0f);

        var mat = NewLineMaterial(color);
        if (mat != null) lr.material = mat;

        for (int i=0;i<lr.positionCount;i++)
        {
            float t =
                i/(float)(lr.positionCount-1);

            Vector3 p =
                Vector3.Lerp(start,end,t);

            if (i>0 && i<lr.positionCount-1)
                p +=
                    UnityEngine.Random.insideUnitSphere*.10f;

            lr.SetPosition(i,p);
        }

        Destroy(go,.16f);
    }

    IEnumerator PlayPulseLightFx(
        GameObject roverGo,
        Color color,
        float peak,
        float duration)
    {
        if (roverGo == null) yield break;

        var fx = new GameObject("PulseLightFX");
        fx.transform.SetParent(roverGo.transform,false);
        fx.transform.localPosition =
            new Vector3(0,1.35f,.25f);

        var light = fx.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.range = 4.2f;
        light.intensity = 0f;

        float elapsed = 0f;
        while (elapsed < duration && fx != null)
        {
            elapsed += Time.deltaTime;
            float p =
                Mathf.Clamp01(elapsed/duration);

            light.intensity =
                Mathf.Sin(p*Mathf.PI)*peak;

            yield return null;
        }

        if (fx != null) Destroy(fx);
    }

    IEnumerator PlayPulseLightAt(
        Transform target,
        Color color,
        float peak,
        float duration)
    {
        if (target == null) yield break;

        var light =
            target.gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.range = 3.2f;
        light.intensity = 0f;

        float elapsed = 0f;
        while (elapsed < duration && target != null)
        {
            elapsed += Time.deltaTime;
            float p =
                Mathf.Clamp01(elapsed/duration);

            if (light != null)
                light.intensity =
                    Mathf.Sin(p*Mathf.PI)*peak;

            yield return null;
        }

        if (light != null) Destroy(light);
    }

    IEnumerator PlayPulseLightAtPoint(
        Vector3 point,
        Color color,
        float peak,
        float duration)
    {
        var fx = new GameObject("ImpactFlash");
        fx.transform.position = point;

        var light = fx.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.range = 4.8f;
        light.intensity = 0f;

        float elapsed = 0f;
        while (elapsed < duration && fx != null)
        {
            elapsed += Time.deltaTime;
            float p =
                Mathf.Clamp01(elapsed/duration);

            light.intensity =
                Mathf.Sin(p*Mathf.PI)*peak;

            yield return null;
        }

        if (fx != null) Destroy(fx);
    }

    Vector3 TerrainNormalAt(float x, float z)
    {
        const float e = .24f;
        float hL = TerrainHeightAt(x-e,z);
        float hR = TerrainHeightAt(x+e,z);
        float hD = TerrainHeightAt(x,z-e);
        float hU = TerrainHeightAt(x,z+e);

        Vector3 n = new Vector3(hL-hR,2f*e,hD-hU);
        return n.normalized;
    }

    IEnumerator MoveRoverAlongPath(GameObject roverGo, List<Vector3> path, float speed)
    {
        if (path == null || path.Count < 2) yield break;

        for (int i = 1; i < path.Count; i++)
        {
            Vector3 target = path[i];
            target.y = TerrainHeightAt(target.x,target.z) + RoverGroundOffset;

            while (Vector3.Distance(roverGo.transform.position,target) > .06f)
            {
                Vector3 flat = target - roverGo.transform.position;
                flat.y = 0f;
                if (flat.sqrMagnitude > .002f)
                {
                    Vector3 terrainNormal = TerrainNormalAt(
                        roverGo.transform.position.x,
                        roverGo.transform.position.z
                    );
                    Vector3 tangentForward = Vector3.ProjectOnPlane(flat.normalized,terrainNormal).normalized;
                    if (tangentForward.sqrMagnitude > .002f)
                    {
                        // У модели перед находится по локальной оси -Z.
                        // Поэтому +Z корня должен смотреть ПРОТИВ направления движения.
                        Quaternion wanted = Quaternion.LookRotation(-tangentForward,terrainNormal);
                        roverGo.transform.rotation = Quaternion.Slerp(
                            roverGo.transform.rotation,wanted,Time.deltaTime*6.5f
                        );
                    }
                }

                float terrainSpeed = TerrainSpeedMultiplierAt(
                    roverGo.transform.position.x,
                    roverGo.transform.position.z
                );
                roverGo.transform.position = Vector3.MoveTowards(
                    roverGo.transform.position,
                    target,
                    speed * terrainSpeed * Time.deltaTime
                );
                yield return null;
            }
            roverGo.transform.position = target;
        }
    }

    void ServiceRover()
    {
        if (selectedRover == null || deliveryAnimating) return;
        var r = selectedRover;

        if (r.status == "Нужен осмотр")
        {
            string[] faults = { "Привод переднего колеса", "Регулятор солнечной панели", "Навигационный лидар" };
            int[] costs = { 45, 60, 75 };
            int index = UnityEngine.Random.Range(0, faults.Length);
            r.damage = faults[index];
            r.repairCost = costs[index];
            r.inspectionDone = true;
            r.status = "Требуется ремонт";
            AddEvent($"ДИАГНОСТИКА // {r.roverName}: {r.damage} // ремонт: {r.repairCost} кр.");
        }
        else if (r.status == "Требуется ремонт")
        {
            if (data.credits < r.repairCost)
            {
                AddEvent($"РЕМОНТ НЕДОСТУПЕН // нужно {r.repairCost} кр., доступно {data.credits} кр.");
                return;
            }
            data.credits -= r.repairCost;
            data.score += 20;
            r.status = "Готов";
            r.damage = "";
            r.repairCost = 0;
            r.inspectionDone = false;
            r.battery = Mathf.Min(100, r.battery + 8);
            AddEvent($"РЕМОНТ ЗАВЕРШЁН // {r.roverName} снова готов к работе");
            UpdateRoverVisual(r);
        }
        Save();
        RefreshUI();
    }

    void NextDay()
    {
        if (deliveryAnimating) return;
        ShowDaySummary();
    }

    void NewGame()
    {
        Time.timeScale = 1f;
        pauseMenuOpen = false;
        if (pauseOverlay != null) pauseOverlay.SetActive(false);
        StopAllCoroutines();
        deliveryAnimating = false;
        selectedRover = null;
        selectedOrder = null;
        CreateFreshData();
        SpawnGameObjects();
        UpdateSelections();
        RefreshUI();
        AddEvent("НОВАЯ ИГРА // данные смены сброшены");
        if (summaryOverlay != null) summaryOverlay.SetActive(false);
    }

    struct RouteRandomEvent
    {
        public string title;
        public string description;
        public float batteryDelta;
        public int creditDelta;
        public int scoreDelta;
        public float riskDelta;
    }

    RouteRandomEvent RollRouteRandomEvent()
    {
        float roll = UnityEngine.Random.value;

        // Примерно половина рейсов проходит без дополнительного события.
        if (roll < .48f)
            return new RouteRandomEvent
            {
                title = "",
                description = "",
                batteryDelta = 0f,
                creditDelta = 0,
                scoreDelta = 0,
                riskDelta = 0f
            };

        if (roll < .62f)
            return new RouteRandomEvent
            {
                title = "РЫХЛЫЙ РЕГОЛИТ",
                description = "Колёса буксуют — дополнительный расход батареи.",
                batteryDelta = -5f,
                creditDelta = 0,
                scoreDelta = 0,
                riskDelta = 0f
            };

        if (roll < .75f)
            return new RouteRandomEvent
            {
                title = "СОЛНЕЧНОЕ ОКНО",
                description = "Панели поймали удачный угол света — часть заряда восстановлена.",
                batteryDelta = 6f,
                creditDelta = 0,
                scoreDelta = 10,
                riskDelta = 0f
            };

        if (roll < .88f)
            return new RouteRandomEvent
            {
                title = "АВАРИЙНЫЙ КОНТЕЙНЕР",
                description = "По пути найден уцелевший контейнер базы.",
                batteryDelta = 0f,
                creditDelta = 35,
                scoreDelta = 20,
                riskDelta = 0f
            };

        return new RouteRandomEvent
        {
            title = "РАДИОПОМЕХИ",
            description = "Связь нестабильна — риск аварии в этом рейсе +8 п.п.",
            batteryDelta = 0f,
            creditDelta = 0,
            scoreDelta = 0,
            riskDelta = .08f
        };
    }

    struct RouteCalc
    {
        public bool canLaunch;
        public float batteryNeeded;
        public string reason;
        public List<Vector3> path;
    }

    RouteCalc CalculateRoute(RoverData r, OrderData o)
    {
        // Сначала проверяем условия, которые не относятся к энергии.
        if (o.status != "Ожидает")
            return new RouteCalc
            {
                canLaunch=false,
                batteryNeeded=0f,
                reason="ЗАКАЗ ЗАКРЫТ",
                path=null
            };

        if (r.status != "Готов")
            return new RouteCalc
            {
                canLaunch=false,
                batteryNeeded=0f,
                reason=$"РОВЕР НЕДОСТУПЕН // {r.status}",
                path=null
            };

        // Проверка грузоподъёмности применяется ко всем заявкам.
        // Во 2-й день две тяжёлые заявки проходят эту проверку только у ВЕКТОРА,
        // а невозможность закрыть обе возникает уже из-за ограниченной батареи.
        if (o.weight > r.capacity)
            return new RouteCalc
            {
                canLaunch=false,
                batteryNeeded=100f,
                reason=$"СЛИШКОМ ТЯЖЁЛЫЙ ГРУЗ // {o.weight:0} кг > лимит {r.capacity:0} кг",
                path=null
            };

        Vector3 start = r.position;
        start.y = TerrainHeightAt(start.x,start.z) + RoverGroundOffset;
        Vector3 end = DeliveryStopPoint(r,o);
        List<Vector3> path = BuildSafeRoute(start,end,r.id);

        if (path == null || path.Count < 2)
            return new RouteCalc
            {
                canLaunch=false,
                batteryNeeded=0f,
                reason="МАРШРУТ ЗАБЛОКИРОВАН ПРЕПЯТСТВИЯМИ",
                path=path
            };

        float weightedDistance = PathTerrainWeightedLength(path);
        float weightMult = 1f + (o.weight / Mathf.Max(1f,r.capacity)) * .55f;

        // Масштаб рассчитан так, чтобы любой допустимый по весу стартовый заказ
        // можно было выполнить хотя бы одним полностью заряженным ровером.
        // Ровер после доставки возвращается на базу, поэтому считаем рейс туда и обратно.
        // 0.78 — игровой масштаб: обычный рейс обычно забирает ~30–60% батареи.
        float rawNeeded = weightedDistance * 2f * .78f * weightMult;
        float needed = Mathf.Clamp(rawNeeded,8f,100f);

        // День 2: осознанный выбор ограниченного ресурса.
        // Обе тяжёлые заявки может взять только ВЕКТОР.
        // Стоимость 47% + 60% = 107%.
        // Даже лучшее батарейное событие (+6%) не позволяет закрыть обе
        // за один день: после первой на вторую всё равно не хватает заряда.
        if (r.id == "R3" && o.id == "D2-MED-HEAVY")
            needed = 47f;
        else if (r.id == "R3" && o.id == "D2-DRILL-HEAVY")
            needed = 60f;

        if (r.battery < needed)
            return new RouteCalc
            {
                canLaunch=false,
                batteryNeeded=needed,
                reason=$"НЕ ХВАТАЕТ БАТАРЕИ // нужно {needed:0}% // доступно {r.battery:0}%",
                path=path
            };

        return new RouteCalc
        {
            canLaunch=true,
            batteryNeeded=needed,
            reason="МАРШРУТ ГОТОВ // РЕЛЬЕФ И РИСК УЧТЕНЫ",
            path=path
        };
    }

    void RefreshUI()
    {
        dayText.text = $"{data.day} / 3";
        creditsText.text = data.credits.ToString();
        scoreText.text = data.score.ToString();
        deliveriesText.text = data.deliveriesToday.ToString();

        if (nextDayButtonText != null)
            nextDayButtonText.text = data.day >= 3 ? "ЗАВЕРШИТЬ СМЕНУ" : "ЗАВЕРШИТЬ ДЕНЬ";

        if (selectedRover == null)
        {
            roverNameText.text = "РОВЕР НЕ ВЫБРАН";
            roverStatusText.text = "НЕ ВЫБРАН";
            roverStatusText.color = muted;
            batteryFill.fillAmount = 0;
            roverDetailsText.text = "Выберите ровер на карте\nили нажмите «Следующий ровер».\n\nСравнивайте заряд, грузоподъёмность\nи техническое состояние.";
            serviceButton.interactable = false;
            serviceText.text = "ДИАГНОСТИКА";
        }
        else
        {
            var r = selectedRover;
            roverNameText.text = r.roverName;
            roverStatusText.text = StatusText(r.status);
            roverStatusText.color = r.status == "Готов" ? green : r.status == "В пути" ? cyan : red;
            batteryFill.fillAmount = Mathf.Clamp01(r.battery/100f);
            batteryFill.color = r.battery > 50 ? cyan : r.battery > 25 ? amber : red;
            string damageLine = string.IsNullOrEmpty(r.damage) ? "Телеметрия: системы в норме" : $"Неисправность: {r.damage}";
            roverDetailsText.text = $"БАТАРЕЯ          {r.battery:0}%\nГРУЗОПОДЪЁМНОСТЬ  {r.capacity:0} кг\nСОСТОЯНИЕ         {r.status}\n\n{damageLine}";

            if (r.status == "Нужен осмотр")
            {
                serviceButton.interactable = true;
                serviceText.text = "ПРОВЕСТИ ДИАГНОСТИКУ";
            }
            else if (r.status == "Требуется ремонт")
            {
                serviceButton.interactable = true;
                serviceText.text = $"РЕМОНТ  •  {r.repairCost} КР.";
            }
            else
            {
                serviceButton.interactable = false;
                serviceText.text = "СИСТЕМЫ В НОРМЕ";
            }
        }

        if (selectedOrder == null)
        {
            orderNameText.text = "ЗАКАЗ НЕ ВЫБРАН";
            riskFill.fillAmount = 0;
            orderDetailsText.text = "Выберите маяк заказа на карте\nили нажмите «Следующий заказ».\n\nВысокая награда обычно означает\nболее сложный и рискованный рейс.";
        }
        else
        {
            var o = selectedOrder;
            orderNameText.text = o.title.ToUpperInvariant();
            riskFill.fillAmount = Mathf.Clamp01(o.risk);
            riskFill.color = o.risk <= .20f ? green : o.risk < .4f ? amber : red;
            orderDetailsText.text = $"ВЕС         {o.weight:0} кг\nНАГРАДА     {o.reward} кр.\nСРОЧНОСТЬ   {UrgencyText(o.urgency)}\nРИСК        {o.risk*100:0}%\nСЕКТОР      {ZoneText(o.zone)}";
        }

        if (selectedRover != null && selectedOrder != null)
        {
            var c = CalculateRoute(selectedRover, selectedOrder);

            routeText.text =
                $"РАСХОД БАТАРЕИ  ~{Mathf.Clamp(c.batteryNeeded,0f,100f):0}%\n{c.reason}";

            routeText.color = c.canLaunch ? green : red;
            launchButton.interactable = c.canLaunch && !deliveryAnimating;
            launchText.text = c.canLaunch ? "ОТПРАВИТЬ РОВЕР" : "МАРШРУТ НЕДОСТУПЕН";
        }
        else
        {
            routeText.text = "ВЫБЕРИТЕ РОВЕР И ЗАКАЗ\nДЛЯ РАСЧЁТА МАРШРУТА";
            routeText.color = muted;
            launchButton.interactable = false;
            launchText.text = "ОТПРАВИТЬ РОВЕР";
        }

        int count = Mathf.Min(3, data.events.Count);
        string feed = "";
        for (int i = data.events.Count-count; i < data.events.Count; i++)
            if (i >= 0) feed += "• " + data.events[i] + (i < data.events.Count-1 ? "\n" : "");
        eventText.text = feed;
        RefreshRouteLine();
    }

    void SetPersistentDamageSmoke(
        GameObject roverGo,
        bool active)
    {
        if (roverGo == null) return;

        Transform existing =
            roverGo.transform.Find(
                "PersistentDamageSmoke");

        GameObject smokeGo =
            existing != null
                ? existing.gameObject
                : null;

        ParticleSystem ps =
            smokeGo != null
                ? smokeGo.GetComponent<ParticleSystem>()
                : null;

        if (smokeGo == null && active)
        {
            smokeGo =
                new GameObject(
                    "PersistentDamageSmoke");

            smokeGo.transform.SetParent(
                roverGo.transform,false);

            smokeGo.transform.localPosition =
                new Vector3(.58f,1.02f,.70f);

            ps =
                smokeGo.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = true;
            main.duration = 1.0f;
            main.startLifetime =
                new ParticleSystem.MinMaxCurve(
                    .72f,1.18f);
            main.startSpeed =
                new ParticleSystem.MinMaxCurve(
                    .18f,.38f);
            main.startSize =
                new ParticleSystem.MinMaxCurve(
                    .11f,.24f);
            main.startColor =
                new ParticleSystem.MinMaxGradient(
                    Hex("62696B"),
                    Hex("343A3D"));
            main.gravityModifier = -.10f;
            main.simulationSpace =
                ParticleSystemSimulationSpace.World;
            main.maxParticles = 40;

            var emission = ps.emission;
            emission.rateOverTime =
                new ParticleSystem.MinMaxCurve(
                    7f,11f);

            var shape = ps.shape;
            shape.shapeType =
                ParticleSystemShapeType.Cone;
            shape.angle = 16f;
            shape.radius = .09f;

            var col =
                ps.colorOverLifetime;
            col.enabled = true;

            Gradient gradient =
                new Gradient();

            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(
                        Hex("707679"),0f),
                    new GradientColorKey(
                        Hex("3F4548"),1f)
                },
                new[]
                {
                    new GradientAlphaKey(
                        .72f,0f),
                    new GradientAlphaKey(
                        .38f,.58f),
                    new GradientAlphaKey(
                        0f,1f)
                });

            col.color = gradient;

            var size =
                ps.sizeOverLifetime;
            size.enabled = true;
            size.size =
                new ParticleSystem.MinMaxCurve(
                    1f,
                    new AnimationCurve(
                        new Keyframe(0f,.55f),
                        new Keyframe(.5f,1.0f),
                        new Keyframe(1f,1.45f)));

            var renderer =
                smokeGo.GetComponent<
                    ParticleSystemRenderer>();

            renderer.renderMode =
                ParticleSystemRenderMode.Billboard;

            var mat =
                NewLineMaterial(
                    Hex("555B5D"));

            if (mat != null)
                renderer.material = mat;
        }

        if (smokeGo == null || ps == null)
            return;

        smokeGo.SetActive(true);

        if (active)
        {
            if (!ps.isPlaying)
                ps.Play();
        }
        else
        {
            ps.Stop(
                true,
                ParticleSystemStopBehavior
                    .StopEmittingAndClear);

            smokeGo.SetActive(false);
        }
    }

    void UpdateRoverVisual(RoverData r)
    {
        if (!roverObjects.ContainsKey(r.id)) return;
        var chassis = roverObjects[r.id].transform.Find("Chassis");
        if (chassis != null)
            SetMaterial(chassis.gameObject, r.status == "Готов" ? Hex("D9E6EC") : Hex("8E5B62"), .15f, .25f);
        bool damaged =
            r.status == "Нужен осмотр" ||
            r.status == "Требуется ремонт";

        // При повреждении показываем только постоянный дым.
        // Он исчезает только после полноценного ремонта.
        SetPersistentDamageSmoke(
            roverObjects[r.id],
            damaged);
    }

    string StatusText(string s)
    {
        if (s == "Готов") return "ГОТОВ";
        if (s == "В пути") return "В ПУТИ";
        if (s == "Нужен осмотр") return "НУЖЕН ОСМОТР";
        if (s == "Требуется ремонт") return "ТРЕБУЕТСЯ РЕМОНТ";
        return s.ToUpperInvariant();
    }

    string ZoneText(string z) => z == "SAFE" ? "Безопасная равнина" : z == "ROUGH" ? "Неровный грунт" : "Кратерное поле";

    string UrgencyText(int u) => u <= 1 ? "КРИТИЧЕСКАЯ" : u == 2 ? "ВЫСОКАЯ" : "ОБЫЧНАЯ";

    void AddEvent(string message)
    {
        data.events.Add(message);
        if (data.events.Count > 40) data.events.RemoveAt(0);
        Debug.Log(message);
        Save();
        RefreshUI();
        string clean = message.Replace(" // ", "  •  ");
        // Не режем уведомление по фиксированному числу символов.
        // Панель сама переносит длинные сообщения и при необходимости слегка уменьшает шрифт.
        ShowToast(clean);
    }

    void Save()
    {
        try { File.WriteAllText(SavePath(), JsonUtility.ToJson(data,true)); } catch { }
    }

    string SavePath() => Path.Combine(Application.persistentDataPath, "moon_courier_crisis_player_v35_compilefix2_save.json");

    void AnimateWorld()
    {
        float pulse = .92f + Mathf.Sin(Time.time * 3.1f) * .08f;

        foreach (var kv in orderObjects)
        {
            if (kv.Value == null) continue;
            var signal = kv.Value.transform.Find("Signal");
            if (signal != null) signal.localScale = Vector3.one * (.34f * pulse);

            var ring = kv.Value.transform.Find("SelectionRing");
            if (ring != null && ring.gameObject.activeSelf)
            {
                // Кольцо "дышит", но каждый кадр снова укладывается по высоте рельефа.
                float t = (Mathf.Sin(Time.time * 3.8f) + 1f) * .5f;
                float ringSize = Mathf.Lerp(1.35f,2.15f,t);

                var lr = ring.GetComponent<LineRenderer>();
                if (lr != null)
                    UpdateTerrainSelectionRing(lr,kv.Value.transform.position,ringSize);
            }

            var glow = kv.Value.transform.Find("SelectionGlow");
            if (glow != null && glow.gameObject.activeSelf)
            {
                var selectionLight = glow.GetComponent<Light>();
                if (selectionLight != null)
                    selectionLight.intensity = 3.8f + Mathf.Sin(Time.time * 4.5f) * .8f;
            }
        }

        foreach (var kv in roverObjects)
        {
            if (kv.Value == null) continue;
            var ring = kv.Value.transform.Find("SelectionRing");
            if (ring != null && ring.gameObject.activeSelf)
                ring.Rotate(0,-42f * Time.deltaTime,0,Space.Self);
        }

        if (cam != null)
        {
            foreach (var label in GameObject.FindGameObjectsWithTag("Player"))
            {
                if (label.name != "WorldLabel") continue;
                // TextMesh рисует лицевую сторону противоположно старому billboard-повороту.
                // Поворачиваем по направлению камеры, чтобы надписи не были зеркальными.
                Vector3 direction = label.transform.position - cam.transform.position;
                if (direction.sqrMagnitude > .01f)
                    label.transform.rotation = Quaternion.LookRotation(direction.normalized, cam.transform.up);
            }
        }
    }

    void CreateWorldLabel(Transform parent, string label, Vector3 localPosition, Color color, int fontSize)
    {
        var go = new GameObject("WorldLabel");
        go.tag = "Player";
        go.transform.SetParent(parent,false);
        go.transform.localPosition = localPosition;

        var mesh = go.AddComponent<TextMesh>();
        mesh.text = label;
        mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        mesh.fontSize = fontSize;
        mesh.characterSize = .055f;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.color = color;
        mesh.fontStyle = FontStyle.Bold;

        var renderer = go.GetComponent<MeshRenderer>();
        if (mesh.font != null && mesh.font.material != null)
            renderer.sharedMaterial = mesh.font.material;
    }

    // ---------- UI helpers ----------
    GameObject MakePanel(Transform parent, string name, Vector2 pos, Vector2 size, Vector2 anchorMin, Vector2 anchorMax, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent,false);
        var img = go.AddComponent<Image>();
        img.color = color;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = anchorMax;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        return go;
    }

    Text MakeText(Transform parent, string value, int size, FontStyle style, Color color)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent,false);
        var t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.text = value; t.fontSize = size; t.fontStyle = style; t.color = color;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    Button MakeButton(Transform parent, string label, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction action, Color bgColor, Color textColor)
    {
        var go = new GameObject(label);
        go.transform.SetParent(parent,false);
        var img = go.AddComponent<Image>(); img.color = bgColor;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = img; btn.onClick.AddListener(action);
        var colors = btn.colors;
        colors.normalColor = bgColor;
        colors.highlightedColor = Color.Lerp(bgColor, Color.white, .12f);
        colors.pressedColor = Color.Lerp(bgColor, Color.black, .18f);
        colors.disabledColor = new Color(.12f,.16f,.19f,.72f);
        btn.colors = colors;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0,1);
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        var txt = MakeText(go.transform,label,13,FontStyle.Bold,textColor);
        txt.alignment = TextAnchor.MiddleCenter;
        Stretch(txt.rectTransform,6,6,6,6);
        return btn;
    }

    Text MakePill(Transform parent, string label, float x, float y, float width)
    {
        var go = new GameObject("Status Pill"); go.transform.SetParent(parent,false);
        var img = go.AddComponent<Image>(); img.color = card2;
        var rt = go.GetComponent<RectTransform>(); rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(0,1); rt.anchoredPosition=new Vector2(x,y); rt.sizeDelta=new Vector2(width,28);
        var t = MakeText(go.transform,label,11,FontStyle.Bold,muted); t.alignment=TextAnchor.MiddleCenter; Stretch(t.rectTransform,5,5,5,5); return t;
    }

    Image MakeBar(Transform parent, float x, float y, float width, float height, Color fillColor)
    {
        var back = new GameObject("Bar"); back.transform.SetParent(parent,false); var bi = back.AddComponent<Image>(); bi.color=Hex("233241");
        var br=back.GetComponent<RectTransform>(); br.anchorMin=br.anchorMax=br.pivot=new Vector2(0,1); br.anchoredPosition=new Vector2(x,y); br.sizeDelta=new Vector2(width,height);
        var fill = new GameObject("Fill"); fill.transform.SetParent(back.transform,false); var fi=fill.AddComponent<Image>(); fi.color=fillColor; fi.type=Image.Type.Filled; fi.fillMethod=Image.FillMethod.Horizontal; fi.fillOrigin=0; fi.fillAmount=0;
        Stretch(fi.rectTransform,0,0,0,0); return fi;
    }

    void MakeSectionLabel(Transform parent, string label, float x, float y)
    {
        var t=MakeText(parent,label,11,FontStyle.Bold,muted); SetRect(t.rectTransform,x,y,250,20,new Vector2(0,1));
    }
    void MakeSmallLabel(Transform parent, string label, float x, float y)
    {
        var t=MakeText(parent,label,10,FontStyle.Bold,muted); SetRect(t.rectTransform,x,y,160,18,new Vector2(0,1));
    }
    void AddAccentLine(Transform parent, Color c, bool bottom)
    {
        var go=new GameObject("Accent"); go.transform.SetParent(parent,false); var i=go.AddComponent<Image>(); i.color=c; var r=go.GetComponent<RectTransform>(); r.anchorMin=new Vector2(0,bottom?0:1); r.anchorMax=new Vector2(1,bottom?0:1); r.pivot=new Vector2(.5f,bottom?0:1); r.sizeDelta=new Vector2(0,2); r.anchoredPosition=Vector2.zero;
    }
    void SetRect(RectTransform rt,float x,float y,float w,float h,Vector2 pivot){rt.anchorMin=rt.anchorMax=pivot;rt.pivot=pivot;rt.anchoredPosition=new Vector2(x,y);rt.sizeDelta=new Vector2(w,h);}
    void Stretch(RectTransform rt,float l,float r,float b,float t){rt.anchorMin=Vector2.zero;rt.anchorMax=Vector2.one;rt.offsetMin=new Vector2(l,b);rt.offsetMax=new Vector2(-r,-t);}
    void StretchX(RectTransform rt,float l,float r,float y,float h){rt.anchorMin=new Vector2(0,1);rt.anchorMax=new Vector2(1,1);rt.pivot=new Vector2(.5f,1);rt.offsetMin=new Vector2(l,-h);rt.offsetMax=new Vector2(-r,0);rt.anchoredPosition=new Vector2(0,y);}
    void AnchorRight(RectTransform rt,float right,float top,float w,float h){rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(1,1);rt.anchoredPosition=new Vector2(-right,-top);rt.sizeDelta=new Vector2(w,h);}

    // ---------- 3D helpers ----------
    GameObject Primitive(PrimitiveType type, Transform parent, string name, Vector3 localPos, Vector3 localScale, Color color)
    {
        var go=GameObject.CreatePrimitive(type); go.name=name; go.transform.SetParent(parent,false); go.transform.localPosition=localPos; go.transform.localScale=localScale; SetMaterial(go,color,.12f,.22f); var col=go.GetComponent<Collider>(); if(col!=null) Destroy(col); return go;
    }
    void LoadRuntimeShaders()
    {
        // Шейдеры лежат в Resources, поэтому Unity гарантированно включает их в Windows Build.
        // Это устраняет ярко-розовые материалы, которые появлялись только в .exe.
        surfaceShader = Resources.Load<Shader>("MoonCourierSurface");
        litShader = Resources.Load<Shader>("MoonCourierLit");
        unlitShader = Resources.Load<Shader>("MoonCourierUnlit");
        lunarAlbedoTexture = Resources.Load<Texture2D>("MoonRegolithAlbedo");
        lunarNormalTexture = Resources.Load<Texture2D>("MoonRegolithNormal");

        if (lunarAlbedoTexture != null)
        {
            lunarAlbedoTexture.wrapMode = TextureWrapMode.Clamp;
            lunarAlbedoTexture.filterMode = FilterMode.Trilinear;
            lunarAlbedoTexture.anisoLevel = 8;
        }
        if (lunarNormalTexture != null)
        {
            lunarNormalTexture.wrapMode = TextureWrapMode.Clamp;
            lunarNormalTexture.filterMode = FilterMode.Trilinear;
            lunarNormalTexture.anisoLevel = 8;
        }

        if (surfaceShader == null) Debug.LogError("Moon Courier Crisis: не найден MoonCourierSurface.shader");
        if (litShader == null) Debug.LogError("Moon Courier Crisis: не найден MoonCourierLit.shader");
        if (unlitShader == null) Debug.LogError("Moon Courier Crisis: не найден MoonCourierUnlit.shader");
    }

    void SetMaterial(GameObject go, Color color, float metallic, float smoothness)
    {
        // Все игровые объекты используют отдельный Lit-шейдер.
        // Лунный процедурный шейдер больше не обесцвечивает роверы, базу и заказы.
        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        Shader shader = litShader != null ? litShader : Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (shader == null) return;

        var m = new Material(shader);
        m.name = "MCC Object Material";
        m.color = color;

        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);

        r.sharedMaterial = m;
    }

    void SetTerrainMaterial(GameObject go, Color color)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        Shader shader = surfaceShader != null ? surfaceShader : litShader;
        if (shader == null) return;

        var m = new Material(shader);
        m.name = "MCC Lunar Surface Material";
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (lunarAlbedoTexture != null && m.HasProperty("_AlbedoTex"))
            m.SetTexture("_AlbedoTex", lunarAlbedoTexture);
        if (lunarNormalTexture != null && m.HasProperty("_NormalTex"))
            m.SetTexture("_NormalTex", lunarNormalTexture);
        if (m.HasProperty("_NormalStrength")) m.SetFloat("_NormalStrength", .30f);
        r.sharedMaterial = m;
    }

    Material NewLineMaterial(Color c)
    {
        Shader shader = unlitShader != null ? unlitShader : surfaceShader;
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) return null;
        var m = new Material(shader);
        m.name = "MCC Line Material";
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }

    static Color Hex(string hex)
    {
        if (ColorUtility.TryParseHtmlString("#"+hex,out Color c)) return c;
        return Color.white;
    }
}

public class MapSelectable : MonoBehaviour
{
    public string id;
    public bool isRover;
}
