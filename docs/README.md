# MateriAI - AI-Powered Material Generator for Unity

## Overview

MateriAI is a Unity editor plugin that generates PBR (Physically Based Rendering) materials using AI. Users describe the material they want in plain text (and optionally provide a reference image), and the plugin communicates with the MateriAI cloud backend to generate a complete texture set — Base Color, Normal Map, and Roughness Map — which can then be saved as native Unity material assets directly within the editor. The plugin is compatible with Built-in, URP, and HDRP rendering pipelines.

## Requirements

- **Unity 2020.3 LTS** or later (also compatible with 2021.x, 2022.x, 2023.x+)
- An active MateriAI account (register at [matgenai.com](https://matgenai.com))
- Internet connection (required for API communication with the generation backend)

## Installation

1. Import the MateriAI package into your Unity project. The plugin files should reside at `Assets/Plugins/MateriAI/`.
2. Unity will automatically compile the editor scripts. No additional setup is required.
3. Open the plugin window via **Window > MateriAI** from the Unity menu bar.

---

## Plugin Architecture

### Folder Structure

```
Assets/Plugins/MateriAI/
├── Editor/
│   ├── MateriAIWindow.cs         # Main editor window (IMGUI)
│   ├── MateriAIGenerator.cs      # API communication & HTTP logic
│   ├── EditorCoroutineRunner.cs  # Editor-time coroutine execution
│   └── MeshRenderUtility.cs      # Preview mesh helper
├── Plugins/
│   └── SharpZipLib/
│       └── ICSharpCode.SharpZipLib.dll  # ZIP extraction dependency
├── README.md
├── CHANGELOG.txt
├── GETTING_STARTED.txt
└── LICENSE.txt
```

All source files are placed under the `Editor/` folder, meaning they are **editor-only** — they are excluded from runtime builds and only available in the Unity Editor.

### Key Classes

| Class | File | Description |
|-------|------|-------------|
| `MaterialAIWindow` | `MateriAIWindow.cs` | The main `EditorWindow` subclass. Implements the full IMGUI interface including login screen, generation controls, 3D material preview, and asset saving. |
| `MaterialAIGenerator` | `MateriAIGenerator.cs` | Static utility class handling all HTTP communication with the MateriAI API. Provides coroutine-based methods for material generation (ZIP) and base texture generation (base64 image). Manages auth token storage via `EditorPrefs`. |
| `EditorCoroutineRunner` | `EditorCoroutineRunner.cs` | A custom editor-time coroutine runner that enables `IEnumerator`-based async workflows in the editor (where Unity's `StartCoroutine` is unavailable). Hooks into `EditorApplication.update` to step through coroutines each editor frame. |
| `MeshRendererUtility` | `MeshRenderUtility.cs` | Static utility that creates temporary `GameObject` primitives to extract `Mesh` references for preview rendering. |

### Dependencies

- **ICSharpCode.SharpZipLib** (`Plugins/SharpZipLib/ICSharpCode.SharpZipLib.dll`) — Used for in-memory ZIP extraction of generated texture archives. The plugin reads ZIP streams directly from the API response bytes without writing to disk first.
- **UnityEngine.Networking** (`UnityWebRequest`) — Used for all HTTP communication.

### Namespace

All plugin classes reside in the `MateriAI.Editor` namespace.

---

## Implementation Details

### Editor Window Lifecycle (`MaterialAIWindow`)

The window is registered as a menu item via `[MenuItem("Window/MateriAI")]` and opened with `EditorWindow.GetWindow<MaterialAIWindow>()`.

**`OnEnable()`**:
1. Restores login state from `EditorPrefs` (`MateriAI_IsLoggedIn`, `MateriAI_Username`). If the user was previously logged in, it immediately fetches current credits from the API.
2. Initializes a `PreviewRenderUtility` for the 3D material preview viewport — sets camera FOV (30°), near clip plane (0.01), light intensity and rotation.
3. Creates a `fallbackMaterial` using the first available shader (`Standard` → `Universal Render Pipeline/Lit` → `HDRP/Lit`) with a gray placeholder texture.

**`OnDisable()`**: Cleans up the `PreviewRenderUtility` and destroys the fallback and preview materials.

**`OnGUI()`**: Delegates to either `DrawLoginScreen()` or `DrawGeneratorScreen()` based on the `isLoggedIn` flag.

### User Interface (IMGUI)

The plugin UI is built entirely with Unity's **IMGUI** system (`EditorGUILayout` / `GUILayout`).

#### Login Screen (`DrawLoginScreen`)
- **Email field** — `EditorGUILayout.TextField`
- **Password field** — `EditorGUILayout.PasswordField` (masked input)
- **Error message** — Displayed via `EditorGUILayout.HelpBox` with `MessageType.Error`
- **Login button** — Triggers `AttemptLogin()` which validates inputs, then starts the `PerformLogin` coroutine
- **Register button** — Opens `https://matgenai.com` in the system browser via `Application.OpenURL`
- **Privacy Policy / Terms links** — Open respective URLs in the browser

#### Generation Screen (`DrawGeneratorScreen`)
- **Credits display** — Shows `creditsRemaining` in bold label, updated after login and after each generation
- **Logout button** — Calls `Logout()` which clears all `EditorPrefs` keys, resets auth token, and switches back to login
- **Preview shape dropdown** — `EditorGUILayout.EnumPopup` switching between `Sphere` and `Cube`
- **3D Material Preview** — A 256×256 interactive preview rendered via `PreviewRenderUtility`:
  - Supports **mouse drag** to rotate the preview (horizontal rotation, vertical pitch clamped to ±89°)
  - Supports **scroll wheel zoom** (clamped between 1–10 units)
  - Renders a primitive mesh (Sphere or Cube obtained via `MeshRendererUtility.GetPrimitiveMesh`) with the current material
- **Model selector** — `EditorGUILayout.Popup` with three tiers: Basic (15 credits), Advanced (20 credits), Professional (50 credits). Each maps to an API tier string via the `modelAPINames` dictionary
- **Prompt field** — `EditorGUILayout.TextArea` (60px height) for entering the text description
- **Reference image picker** — `EditorGUILayout.ObjectField` accepting `Texture2D` assets (drag-and-drop from the Project window)
- **Seamless toggle** — `EditorGUILayout.ToggleLeft` that adds 50 credits to the generation cost
- **Generate button** — Starts the generation pipeline with retry logic (up to 3 attempts)
- **Save Material to Project button** — Visible only after successful generation. Saves textures and material as Unity assets

### Authentication Flow

1. User enters email and password and clicks **Login**.
2. `AttemptLogin()` validates that both fields are non-empty.
3. `PerformLogin()` coroutine is started via `EditorCoroutineRunner.Start()`:
   - Serializes a `LoginRequest` object (`{ email, password }`) to JSON using `JsonUtility`.
   - Sends a `POST` request to `{ApiBaseUrl}/auth/login` with `Content-Type: application/json`.
   - On **success (2xx)**: Parses `LoginResponse` to extract `access_token`, stores it via `MaterialAIGenerator.SetAuthorizationToken()` (which writes to `EditorPrefs` key `MateriAI_AuthToken`). Saves login state to `EditorPrefs`. Starts `FetchUserCredits()` coroutine.
   - On **failure**: Parses the error response and provides user-friendly messages:
     - HTTP 404 / "not found" → "Email not found. Please check your email or create an account."
     - HTTP 401 / "password"/"invalid" → "Incorrect password. Please try again."
     - Other errors → Displays the server's error message or a generic fallback.

### Session Persistence

Login state is persisted across Unity editor sessions using `EditorPrefs`:
- `MateriAI_IsLoggedIn` (bool) — Whether the user is logged in
- `MateriAI_Username` (string) — Stored email
- `MateriAI_Credits` (int) — Cached credit balance
- `MateriAI_AuthToken` (string) — The Bearer token for API requests

On `Logout()`, all keys are deleted and the auth token is cleared.

### Editor Coroutine System (`EditorCoroutineRunner`)

Since `MonoBehaviour.StartCoroutine` is unavailable in editor scripts, the plugin implements a custom coroutine runner:

- **`EditorCoroutineRunner.Start(IEnumerator)`** — Pushes the coroutine onto a stack-based `CoroutineState` and subscribes to `EditorApplication.update`.
- **`Update()`** — Called every editor frame. Steps through each active coroutine state.
- **`Step(CoroutineState)`** — Handles yielded values:
  - `AsyncOperation` (e.g., `UnityWebRequest.SendWebRequest()`) — Waits until `isDone`.
  - `CustomYieldInstruction` — Waits while `keepWaiting` is true.
  - `IEnumerator` — Pushes nested coroutines onto the stack (enabling `yield return` of sub-coroutines).
  - Other values — Waits one update frame.
- Automatically unsubscribes from `EditorApplication.update` when all coroutines complete.

This enables the plugin to perform async HTTP requests, chained coroutines, and multi-step workflows entirely within the editor.

### Material Generation Pipeline

#### Request Construction (`MaterialAIGenerator.GenerateMaterial`)

The static method `GenerateMaterial` is a coroutine that:
1. Determines the endpoint based on whether a reference image is provided:
   - Text-only: `POST {GenerateApiUrl}/generate/generate-zip-from-text` with JSON body (`{ prompt, tier, seamless }`)
   - With image: `POST {GenerateApiUrl}/generate/generate-zip-from-image` with `WWWForm` multipart data (prompt, tier, seamless fields + image binary encoded as PNG)
2. For image uploads, the texture is first made readable via `MakeReadable()`:
   - Blits the source texture to a temporary `RenderTexture` (ARGB32)
   - Reads pixels back into a new `Texture2D` with `ReadPixels()` (necessary because asset textures may not be CPU-readable)
   - Encodes to PNG via `EncodeToPNG()`
3. Attaches the `Authorization: Bearer <token>` header from `EditorPrefs`.
4. Sends the request and waits for the response.
5. On success: Parses the JSON response to extract `download_url`, then issues a `GET` request to download the ZIP file. Returns the raw ZIP bytes via the `onSuccess` callback.
6. On failure: Invokes the `onError` callback with the error message.

#### Alternative: Base Texture Generation (`MaterialAIGenerator.GenerateBaseTexture`)

A separate method for generating only the base/albedo texture:
- Endpoints: `generate-base-image` (text) / `generate-base-image-with-image` (with reference)
- Returns a base64-encoded image in the response JSON (`LambdaImageResponse.body`)
- Decodes base64 → byte[] → `Texture2D.LoadImage()` and returns the texture via callback

#### Retry Logic

The generation screen wraps API calls in `RetryCoroutine<T>()`:
- Attempts the operation up to `maxRetries` (default: 3) times
- On each attempt, runs the coroutine and checks for success/failure callbacks
- If all attempts fail, invokes the failure callback with an error message

#### ZIP Extraction & Texture Loading (`OnZipReceived`)

When the ZIP bytes arrive:
1. Creates a new `Material` using the best available shader (`Standard` → `URP/Lit` → `HDRP/Lit`).
2. Opens the ZIP data as an in-memory stream using `ICSharpCode.SharpZipLib.Zip.ZipInputStream`.
3. Iterates through ZIP entries, reading each into a `MemoryStream` → byte array → `Texture2D.LoadImage()`.
4. Maps textures by filename:
   - `base_texture.png` → `_MainTex` (albedo/base color)
   - `normal_map.png` → `_BumpMap` (with `_NORMALMAP` keyword enabled)
   - `roughness_map.png` → `_MetallicGlossMap` (with `_METALLICGLOSSMAP` keyword enabled, `_GlossMapScale` set to 0.1)
5. Stores the parsed textures in `baseTex`, `normalTex`, `roughnessTex` member variables.
6. Triggers a credit refresh and repaints the window.

### 3D Material Preview

The preview system uses Unity's `PreviewRenderUtility`:
- **`DrawMaterialPreview(Rect, Material)`** — Begins a preview render, draws the selected primitive mesh (Sphere or Cube) with the current material and a rotation matrix computed from user input, renders the camera, and displays the result texture in the GUI rect.
- **`HandleMouseInput(Rect)`** — Processes mouse events within the preview rect:
  - `MouseDown` → Captures the hot control
  - `MouseDrag` → Updates `previewRotation` (x = horizontal, y = vertical, clamped ±89°)
  - `ScrollWheel` → Adjusts `zoom` (clamped 1–10)
  - `MouseUp` → Releases the hot control
- **`MeshRendererUtility.GetPrimitiveMesh(PrimitiveType)`** — Creates a temporary `GameObject.CreatePrimitive()`, extracts the `MeshFilter.sharedMesh`, destroys the temporary object, and returns the mesh.

### Saving Materials as Unity Assets

When the user clicks **Save Material to Project** (`SaveMaterialAndTexturesToAssets`):

1. **Creates the output folder**: `Assets/MateriAI/GeneratedMaterials/Material_{timestamp}/` (creates parent folders if they don't exist using `AssetDatabase.CreateFolder`).

2. **Saves textures as PNG assets**: Each texture (`baseTex`, `normalTex`, `roughnessTex`) is:
   - Encoded to PNG via `Texture2D.EncodeToPNG()`
   - Written to disk via `File.WriteAllBytes()`
   - Imported via `AssetDatabase.ImportAsset()`
   - The `TextureImporter` is configured: normal maps get `TextureImporterType.NormalMap`, others get `TextureImporterType.Default`
   - `importer.SaveAndReimport()` is called to apply settings

3. **Creates the Material asset**: A new `Material` is created with the same shader as the preview material, then:
   - Base Color texture → `_MainTex`
   - Normal Map texture → `_BumpMap` (with `_NORMALMAP` keyword enabled)
   - Roughness Map texture → `_MetallicGlossMap` (with `_METALLICGLOSSMAP` keyword enabled, `_GlossMapScale = 0.1`)

4. **Saves to disk**: `AssetDatabase.CreateAsset()` → `AssetDatabase.SaveAssets()` → `AssetDatabase.Refresh()`

5. **Post-save**: Displays a confirmation dialog, focuses the Project window, and selects the new material asset.

### Credit System

- After login and after each successful generation, `FetchUserCredits()` sends a `GET` request to `{ApiBaseUrl}/users/me/credits` with the Bearer auth token.
- The response JSON structure is `{ "data": { "credits": <int>, "total_used": <int> } }` (deserialized via `CreditsResponse` / `CreditsData` classes).
- The credit balance is displayed in the UI header and cached in `EditorPrefs`.
- Credit costs per model tier: Basic (15), Advanced (20), Professional (50). Seamless mode adds 50 credits.
- The `GetCreditsForModel(int)` helper method computes the total cost.

### Render Pipeline Compatibility

The plugin automatically selects the appropriate shader for the current rendering pipeline:

```csharp
Shader shader = Shader.Find("Standard")           // Built-in RP
             ?? Shader.Find("Universal Render Pipeline/Lit")  // URP
             ?? Shader.Find("HDRP/Lit");           // HDRP
```

This shader fallback chain runs at both preview material creation and save time, ensuring the generated materials work regardless of which pipeline the project uses.

---

## API Endpoints

The plugin communicates with two backend services:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `{ApiBaseUrl}/auth/login` | POST | Authenticates the user with email/password. Returns an access token. |
| `{ApiBaseUrl}/users/me/credits` | GET | Fetches the authenticated user's remaining credit balance. |
| `{GenerateApiUrl}/generate/generate-zip-from-text` | POST | Generates a PBR texture set from a text prompt. Returns a ZIP download URL. |
| `{GenerateApiUrl}/generate/generate-zip-from-image` | POST | Generates a PBR texture set from a text prompt + reference image. Returns a ZIP download URL. |
| `{GenerateApiUrl}/generate/generate-base-image` | POST | Generates only the base/albedo texture from a text prompt. Returns a base64-encoded image. |
| `{GenerateApiUrl}/generate/generate-base-image-with-image` | POST | Generates only the base/albedo texture from a text prompt + reference image. Returns a base64-encoded image. |

All authenticated requests include an `Authorization: Bearer <token>` header.

---

## Generated Output Format

The generation API produces a ZIP archive containing three PNG texture maps:
- `base_texture.png` — The albedo/base color map
- `normal_map.png` — The tangent-space normal map
- `roughness_map.png` — The roughness map

These are extracted in-memory using SharpZipLib, loaded into `Texture2D` objects, and can be saved as standard Unity assets.

---

## C# API Reference

### `MaterialAIGenerator`

```csharp
// Generate a full PBR material texture set (ZIP containing base, normal, roughness)
public static IEnumerator GenerateMaterial(
    string prompt,
    Texture2D image,          // null for text-only
    string tier,              // "basic", "advanced", or "ultra-advanced"
    bool seamless,
    Action<byte[]> onSuccess, // ZIP bytes
    Action<string> onError
);

// Generate only the base/albedo texture
public static IEnumerator GenerateBaseTexture(
    string prompt,
    Texture2D image,           // null for text-only
    string tier,
    bool seamless,
    Action<Texture2D> onSuccess,
    Action<string> onError
);

// Store/retrieve auth token
public static void SetAuthorizationToken(string token);
public static string GetApiBaseUrl();
```

### `EditorCoroutineRunner`

```csharp
// Start an IEnumerator coroutine in the editor
public static void Start(IEnumerator routine);
```

### `MeshRendererUtility`

```csharp
// Get a mesh for a Unity primitive type (Sphere, Cube, etc.)
public static Mesh GetPrimitiveMesh(PrimitiveType type);
```

---

## User Workflow Summary

1. **Open** the MateriAI panel via **Window > MateriAI**.
2. **Log in** with your MateriAI account credentials.
3. **Select a model** tier (Basic, Advanced, or Professional).
4. **Enter a prompt** describing the desired material (e.g., "weathered red brick with moss growth").
5. *(Optional)* **Drag a reference image** from the Project window into the reference image field.
6. *(Optional)* **Enable Seamless** for tileable textures.
7. Click **Generate** and wait for the AI to produce the texture set (with automatic retry on failure).
8. **Preview** the result on an interactive 3D sphere/cube — drag to rotate, scroll to zoom.
9. Click **Save Material to Project** to import the textures and create a ready-to-use Unity Material asset at `Assets/MateriAI/GeneratedMaterials/`.

---

## Troubleshooting

### Login Issues
- Ensure your internet connection is stable.
- Verify your MateriAI credentials at [matgenai.com](https://matgenai.com).
- Check that your account has available credits.

### Generation Failed
- Check your internet connection.
- Ensure you have sufficient credits for the selected tier.
- Try a different or simpler prompt.
- Check the Unity Console for `[MateriAI]` error messages.

### Plugin Not Appearing
- Ensure the plugin is at `Assets/Plugins/MateriAI/`.
- Restart the Unity Editor.
- Check the Console for compilation errors.

---

## Support

For questions, bug reports, or feature requests, contact us at matgenai.office@gmail.com or visit [matgenai.com](https://matgenai.com).

## License

Copyright MateriAI 2026. All rights reserved.
