using System.Collections;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;

public static class MaterialAIGenerator
{
    private static readonly string baseUrl = "https://nn74i2a85m.execute-api.us-east-1.amazonaws.com/prod/api/v1/generate";

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

        if (request.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke(request.error);
            yield break;
        }

        LambdaZipResponse response = null;
        try
        {
            var responseJson = request.downloadHandler.text;
            response = JsonUtility.FromJson<LambdaZipResponse>(responseJson);
        }
        catch (System.Exception ex)
        {
            onError?.Invoke($"Failed to parse download URL: {ex.Message}");
            yield break;
        }

        if (response == null || string.IsNullOrEmpty(response.download_url))
        {
            onError?.Invoke("Download URL is missing.");
            yield break;
        }

        UnityWebRequest downloadRequest = UnityWebRequest.Get(response.download_url);
        downloadRequest.downloadHandler = new DownloadHandlerBuffer();

        yield return downloadRequest.SendWebRequest();

        if (downloadRequest.result == UnityWebRequest.Result.Success)
        {
            byte[] zipData = downloadRequest.downloadHandler.data;
            onSuccess?.Invoke(zipData);
        }
        else
        {
            onError?.Invoke($"Failed to download ZIP: {downloadRequest.error}");
        }
    }

    [System.Serializable]
    private class LambdaZipResponse
    {
        public string download_url;
    }
    
    public static IEnumerator GenerateBaseTexture(string prompt, Texture2D image, System.Action<Texture2D> onSuccess, System.Action<string> onError)
    {
        UnityWebRequest request;

        if (image is null)
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
            Texture2D readableImage = MakeReadable(image);
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
            try
            {
                var responseJson = request.downloadHandler.text;
                var response = JsonUtility.FromJson<LambdaImageResponse>(responseJson);

                byte[] imageData = System.Convert.FromBase64String(response.body);

                Texture2D tex = new Texture2D(2, 2);
                if (tex.LoadImage(imageData))
                {
                    onSuccess?.Invoke(tex);
                }
                else
                {
                    onError?.Invoke("Failed to load image from decoded base64.");
                }
            }
            catch (System.Exception ex)
            {
                onError?.Invoke($"Failed to parse or load image: {ex.Message}");
            }
        }
        else
        {
            onError?.Invoke(request.error);
        }
    }

    [System.Serializable]
    private class LambdaImageResponse
    {
        public int statusCode;
        public LambdaHeaders headers;
        public bool isBase64Encoded;
        public string body;
    }

    [System.Serializable]
    private class LambdaHeaders
    {
        public string ContentType;
        public string ContentDisposition;
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
