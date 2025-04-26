using System.Collections;
using UnityEngine;
using UnityEditor;
using System.IO;
using Unity.EditorCoroutines.Editor;

public class MaterialAIWindow : EditorWindow
{
    private string prompt = "";
    private Texture2D referenceImage;
    private bool isGenerating = false;
    private bool generateOnlyBaseTexture = false;

    private Texture2D baseTex, normalTex, roughnessTex;
    private Material previewMaterial;
    private Material fallbackMaterial;
    private PreviewRenderUtility previewUtility;
    private Vector2 previewRotation = new Vector2(120f, -20f);
    private Vector2 dragStart;
    private float zoom = 3f;

    private enum PreviewShape { Sphere, Cube }
    private PreviewShape previewShape = PreviewShape.Sphere;

    [MenuItem("Tools/MateriAI")]
    public static void ShowWindow() => GetWindow<MaterialAIWindow>("MateriAI");

    private void OnEnable()
    {
        previewUtility = new PreviewRenderUtility();
        previewUtility.cameraFieldOfView = 30f;
        previewUtility.camera.nearClipPlane = 0.01f;
        previewUtility.lights[0].intensity = 1f;
        previewUtility.lights[0].transform.rotation = Quaternion.Euler(30f, 30f, 0);
        previewUtility.camera.transform.LookAt(Vector3.zero);

        Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("HDRP/Lit");
        fallbackMaterial = new Material(shader);
        fallbackMaterial.SetTexture("_MainTex", GenerateGrayTexture());
    }

    private void OnDisable()
    {
        previewUtility?.Cleanup();
        DestroyImmediate(fallbackMaterial);
        DestroyImmediate(previewMaterial);
    }

    private void OnGUI()
    {
        GUILayout.Space(10);

        if (!generateOnlyBaseTexture)
        {
            previewShape = (PreviewShape)EditorGUILayout.EnumPopup("Preview Shape", previewShape);
            GUILayout.Space(10);
        }
        else
        {
            EditorGUILayout.LabelField("Preview 2D Image", EditorStyles.boldLabel);
            GUILayout.Space(10);
        }
        
        Rect previewRect = GUILayoutUtility.GetRect(256, 256, GUILayout.ExpandWidth(true));

        if (!generateOnlyBaseTexture)
        {
            HandleMouseInput(previewRect);
            DrawMaterialPreview(previewRect, previewMaterial ?? fallbackMaterial);
        }
        else if (baseTex is not null)
        {
            GUI.DrawTexture(previewRect, baseTex, ScaleMode.ScaleToFit, true);
        }
        else
        {
            GUI.Box(previewRect, "2D Preview will appear here", EditorStyles.centeredGreyMiniLabel);
        }

        GUILayout.Space(10);

        GUILayout.Label("Prompt:");
        prompt = EditorGUILayout.TextArea(prompt, GUILayout.Height(60));
        GUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Reference Image (optional):", GUILayout.Width(160));
        referenceImage = (Texture2D)EditorGUILayout.ObjectField(referenceImage, typeof(Texture2D), false);
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(4);
        
        generateOnlyBaseTexture = EditorGUILayout.ToggleLeft("Generate Only Base Texture (2D)", generateOnlyBaseTexture);

        GUILayout.Space(20);

        if (isGenerating)
        {
            GUILayout.Label("Generating...", EditorStyles.miniLabel);
        }

        if (GUILayout.Button("Generate") && !isGenerating)
        {
            isGenerating = true;
            baseTex = normalTex = roughnessTex = null;
            DestroyImmediate(previewMaterial);
            previewMaterial = null;
        
            if (generateOnlyBaseTexture)
            {
                EditorCoroutineUtility.StartCoroutine(
                    RetryCoroutine<Texture2D>(
                        (onSuccess, onError) => MaterialAIGenerator.GenerateBaseTexture(prompt, referenceImage, onSuccess, onError),
                        tex => {
                            baseTex = tex;
                            Shader shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
                            previewMaterial = new Material(shader);
                            previewMaterial.mainTexture = baseTex;
                            isGenerating = false;
                            Repaint();
                        },
                        error => {
                            Debug.LogError(error);
                            isGenerating = false;
                            ShowNotification(new GUIContent(error));
                        }
                    ),
                    this);
            }
            else
            {
                EditorCoroutineUtility.StartCoroutine(
                    RetryCoroutine<byte[]>(
                        (onSuccess, onError) => MaterialAIGenerator.GenerateMaterial(prompt, referenceImage, onSuccess, onError),
                        OnZipReceived,
                        error => {
                            Debug.LogError(error);
                            isGenerating = false;
                            ShowNotification(new GUIContent(error));
                        }
                    ),
                    this);
            }
        }

        GUILayout.Space(4);
        if (previewMaterial is not null && !isGenerating)
        {
            if (GUILayout.Button(generateOnlyBaseTexture ? "Save Texture to Project" : "Save Material to Project"))
            {
                if (generateOnlyBaseTexture)
                    SaveBaseTexture();
                else
                    SaveMaterialAndTexturesToAssets();
            }
        }
    }

    private void SaveBaseTexture()
    {
        string rootPath = "Assets/MateriAI/GeneratedTextures";
        if (!AssetDatabase.IsValidFolder("Assets/MateriAI"))
            AssetDatabase.CreateFolder("Assets", "MateriAI");
        if (!AssetDatabase.IsValidFolder(rootPath))
            AssetDatabase.CreateFolder("Assets/MateriAI", "GeneratedTextures");

        string textureName = "Texture_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        string path = Path.Combine(rootPath, textureName + ".png");
        byte[] pngData = baseTex.EncodeToPNG();
        File.WriteAllBytes(path, pngData);

        AssetDatabase.ImportAsset(path);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer is not null)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
        }

        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Saved", "Base texture saved!", "OK");
        EditorUtility.FocusProjectWindow();
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private void SaveMaterialAndTexturesToAssets()
    {
        string rootPath = "Assets/MateriAI/GeneratedMaterials";
        if (!AssetDatabase.IsValidFolder("Assets/MateriAI"))
            AssetDatabase.CreateFolder("Assets", "MateriAI");
        if (!AssetDatabase.IsValidFolder(rootPath))
            AssetDatabase.CreateFolder("Assets/MateriAI", "GeneratedMaterials");

        string materialFolderName = "Material_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string materialFolderPath = Path.Combine(rootPath, materialFolderName);
        AssetDatabase.CreateFolder(rootPath, materialFolderName);

        string basePath = Path.Combine(materialFolderPath, "BaseTexture.png");
        string normalPath = Path.Combine(materialFolderPath, "NormalMap.png");
        string roughnessPath = Path.Combine(materialFolderPath, "RoughnessMap.png");

        if (baseTex is not null)
            SaveTextureAsset(baseTex, basePath);
        if (normalTex is not null)
            SaveTextureAsset(normalTex, normalPath, true);
        if (roughnessTex is not null)
            SaveTextureAsset(roughnessTex, roughnessPath);

        Texture2D baseTexAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath);
        Texture2D normalTexAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
        Texture2D roughnessTexAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(roughnessPath);

        Material mat = new Material(previewMaterial.shader);
        if (baseTexAsset is not null) mat.SetTexture("_MainTex", baseTexAsset);
        if (normalTexAsset is not null)
        {
            mat.SetTexture("_BumpMap", normalTexAsset);
            mat.EnableKeyword("_NORMALMAP");
        }
        if (roughnessTexAsset is not null)
        {
            mat.SetTexture("_MetallicGlossMap", roughnessTexAsset);
            mat.EnableKeyword("_METALLICGLOSSMAP");
            mat.SetFloat("_GlossMapScale", 0.1f);
        }

        string matPath = Path.Combine(materialFolderPath, "GeneratedMaterial.mat");
        AssetDatabase.CreateAsset(mat, matPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Material Saved", $"Saved to:\n{materialFolderPath}", "OK");

        EditorUtility.FocusProjectWindow();
        Selection.activeObject = mat;
    }

    private void SaveTextureAsset(Texture2D tex, string path, bool isNormalMap = false)
    {
        byte[] pngData = tex.EncodeToPNG();
        if (pngData == null) return;

        File.WriteAllBytes(path, pngData);
        AssetDatabase.ImportAsset(path);

        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer is not null)
        {
            importer.textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.SaveAndReimport();
        }
    }

    private void HandleMouseInput(Rect previewRect)
    {
        int controlID = GUIUtility.GetControlID(FocusType.Passive);
        Event evt = Event.current;

        if (evt.type == EventType.MouseDown && previewRect.Contains(evt.mousePosition))
        {
            GUIUtility.hotControl = controlID;
            dragStart = evt.mousePosition;
            evt.Use();
        }

        if (evt.type == EventType.MouseDrag && GUIUtility.hotControl == controlID)
        {
            Vector2 delta = evt.mousePosition - dragStart;
            dragStart = evt.mousePosition;
            previewRotation.x += delta.x;
            previewRotation.y = Mathf.Clamp(previewRotation.y + delta.y, -89f, 89f);
            evt.Use();
            Repaint();
        }

        if (evt.type == EventType.ScrollWheel && previewRect.Contains(evt.mousePosition))
        {
            zoom += evt.delta.y * 0.2f;
            zoom = Mathf.Clamp(zoom, 1f, 10f);
            evt.Use();
            Repaint();
        }

        if (evt.type == EventType.MouseUp && GUIUtility.hotControl == controlID)
        {
            GUIUtility.hotControl = 0;
            evt.Use();
        }
    }

    private void DrawMaterialPreview(Rect rect, Material mat)
    {
        if (generateOnlyBaseTexture && baseTex is not null)
        {
            // Just draw the texture directly
            GUI.DrawTexture(rect, baseTex, ScaleMode.ScaleToFit, true);
            return;
        }

        previewUtility.BeginPreview(rect, GUIStyle.none);

        Quaternion rotation = Quaternion.Euler(previewRotation.y, -previewRotation.x, 0);
        Matrix4x4 matrix = Matrix4x4.TRS(Vector3.zero, rotation, Vector3.one);

        PrimitiveType type = previewShape == PreviewShape.Sphere ? PrimitiveType.Sphere : PrimitiveType.Cube;
        Mesh mesh = MeshRendererUtility.GetPrimitiveMesh(type);
        previewUtility.DrawMesh(mesh, matrix, mat, 0);

        previewUtility.camera.transform.position = new Vector3(0, 0, -zoom);
        previewUtility.camera.transform.LookAt(Vector3.zero);
        previewUtility.camera.Render();

        Texture result = previewUtility.EndPreview();
        GUI.DrawTexture(rect, result, ScaleMode.ScaleToFit, false);
    }


    private void OnZipReceived(byte[] zipBytes)
    {
        isGenerating = false;

        Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("HDRP/Lit");
        previewMaterial = new Material(shader);

        using var memoryStream = new MemoryStream(zipBytes);
        using var zipStream = new ICSharpCode.SharpZipLib.Zip.ZipInputStream(memoryStream);

        ICSharpCode.SharpZipLib.Zip.ZipEntry entry;
        while ((entry = zipStream.GetNextEntry()) != null)
        {
            using var entryStream = new MemoryStream();
            zipStream.CopyTo(entryStream);
            byte[] data = entryStream.ToArray();

            Texture2D tex = new Texture2D(2, 2);
            tex.LoadImage(data);

            if (entry.Name == "base_texture.png")
            {
                baseTex = tex;
                previewMaterial.SetTexture("_MainTex", baseTex);
            }
            else if (entry.Name == "normal_map.png")
            {
                normalTex = tex;
                previewMaterial.SetTexture("_BumpMap", normalTex);
                previewMaterial.EnableKeyword("_NORMALMAP");
            }
            else if (entry.Name == "roughness_map.png")
            {
                roughnessTex = tex;
                previewMaterial.SetTexture("_MetallicGlossMap", roughnessTex);
                previewMaterial.EnableKeyword("_METALLICGLOSSMAP");
                previewMaterial.SetFloat("_GlossMapScale", 0.1f);
            }
        }

        Repaint();
    }

    private Texture2D GenerateGrayTexture()
    {
        Texture2D tex = new Texture2D(2, 2);
        Color gray = new Color(0.5f, 0.5f, 0.5f, 1f);
        tex.SetPixels(new[] { gray, gray, gray, gray });
        tex.Apply();
        return tex;
    }
    
    private IEnumerator RetryCoroutine<T>(System.Func<System.Action<T>, System.Action<string>, IEnumerator> action, System.Action<T> onSuccess, System.Action<string> onFailure, int maxRetries = 3)
    {
        int attempts = 0;
        bool success = false;
    
        while (attempts < maxRetries && !success)
        {
            bool isDone = false;
            string error = null;
            T result = default;
    
            yield return action(
                res => {
                    result = res;
                    success = true;
                    isDone = true;
                },
                err => {
                    error = err;
                    isDone = true;
                });
    
            while (!isDone) yield return null;
    
            if (success)
            {
                onSuccess(result);
                yield break;
            }
    
            attempts++;
        }
    
        onFailure("❌ Generation failed after multiple attempts.");
    }

}
