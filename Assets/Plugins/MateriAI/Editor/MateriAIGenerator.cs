using System.Collections;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;

public static class MaterialAIGenerator
{
    private static readonly string baseUrl = "http://localhost:8000/api/v1/generate";

    public static IEnumerator GenerateMaterial(string prompt, Texture2D image, System.Action<byte[]> onSuccess, System.Action<string> onError)
    {
        UnityWebRequest request;

        if (image is null)
        {
            var json = JsonUtility.ToJson(new PromptWrapper { prompt = prompt });
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

            request = new UnityWebRequest($"{baseUrl}/generate-zip-from-text", "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyRaw)
            {
                contentType = "application/json"
            };
        }
        else
        {
            var form = new WWWForm();
            form.AddField("prompt", prompt);
            Texture2D readableImage = MakeReadable(image);
            form.AddBinaryData("image", readableImage.EncodeToPNG(), "ref.png", "image/png");

            request = UnityWebRequest.Post($"{baseUrl}/generate-zip-from-image", form);
        }

        request.downloadHandler = new DownloadHandlerBuffer();
        request.disposeUploadHandlerOnDispose = true;
        request.disposeDownloadHandlerOnDispose = true;

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            onSuccess?.Invoke(request.downloadHandler.data);
        }
        else
        {
            onError?.Invoke(request.error);
        }
    }
    
    public static IEnumerator GenerateBaseTexture(string prompt, Texture2D image, System.Action<Texture2D> onSuccess, System.Action<string> onError)
    {
        UnityWebRequest request;

        if (image == null)
        {
            var json = JsonUtility.ToJson(new PromptWrapper { prompt = prompt });
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

            request = new UnityWebRequest($"{baseUrl}/generate-base-image", "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyRaw)
            {
                contentType = "application/json"
            };
        }
        else
        {
            Texture2D readableImage = MakeReadable(image); // 👈 still unsafe if image is null!
            if (readableImage is null)
            {
                onError?.Invoke("Reference image is invalid.");
                yield break;
            }

            var form = new WWWForm();
            form.AddField("prompt", prompt);
            form.AddBinaryData("image", readableImage.EncodeToPNG(), "input.png", "image/png");

            request = UnityWebRequest.Post($"{baseUrl}/generate-base-image-with-image", form);
        }

        request.downloadHandler = new DownloadHandlerBuffer();
        request.disposeUploadHandlerOnDispose = true;
        request.disposeDownloadHandlerOnDispose = true;

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            byte[] imageData = request.downloadHandler.data;
            Texture2D tex = new Texture2D(2, 2);
            if (tex.LoadImage(imageData))
            {
                onSuccess?.Invoke(tex);
            }
            else
            {
                onError?.Invoke("Failed to load image from response.");
            }
        }
        else
        {
            onError?.Invoke(request.error);
        }
    }
    
    private static Texture2D MakeReadable(Texture2D source)
    {
        if (source is null)
        {
            Debug.LogWarning("⚠ MakeReadable called with null source texture.");
            return null;
        }

        RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;

        Texture2D readableTex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        readableTex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        readableTex.Apply();

        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        return readableTex;
    }



    [System.Serializable]
    private class PromptWrapper
    {
        public string prompt;
    }
}
