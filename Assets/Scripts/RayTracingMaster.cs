using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

public class RayTracingMaster : MonoBehaviour
{
    public ComputeShader shader;
    public Texture skyboxTexture;
    private int computeKernel;

    public Vector2 sphereRadiusRange = new Vector2(3.0f, 8.0f);
    public uint maxNumSpheres = 100;
    public float spherePlacementRadius = 100.0f;
    public int sphereSeed;

    private RenderTexture target;
    private Camera cam;
    private uint currentSamples;
    private Material convergeMaterial;
    private RenderTexture converged;

    private static readonly List<RayTracingObject> rayTracingObjects = new List<RayTracingObject>();
    private static readonly List<MeshObject> meshObjects = new List<MeshObject>();
    private static readonly List<Vector3> vertices = new List<Vector3>();
    private static readonly List<int> indices = new List<int>();

    private ComputeBuffer sphereBuffer;
    private ComputeBuffer meshObjectBuffer;
    private ComputeBuffer vertexBuffer;
    private ComputeBuffer indexBuffer;

    private static bool meshObjectsNeedsRebuilding;

    private struct Sphere
    {
        public Vector3 position;
        public float radius;
        public Vector3 albedo;
        public Vector3 specular;
        public float smoothness;
        public Vector3 emission;
    }

    private struct MeshObject
    {
        public Matrix4x4 localToWorldMatrix;
        public int indicesOffset;
        public int indicesCount;
    }

    public static void RegisterObject(RayTracingObject obj)
    {
        rayTracingObjects.Add(obj);
        meshObjectsNeedsRebuilding = true;
    }

    public static void UnregisterObject(RayTracingObject obj)
    {
        rayTracingObjects.Remove(obj);
        meshObjectsNeedsRebuilding = true;
    }
    private void Awake()
    {
        cam = GetComponent<Camera>();
    }

    private void OnEnable()
    {
        currentSamples = 0;
        computeKernel = shader.FindKernel("CSMain");
        SetUpScene();
    }

    private void OnDisable()
    {
        sphereBuffer?.Release();
        meshObjectBuffer?.Release();
        vertexBuffer?.Release();
        indexBuffer?.Release();
    }

    private void SetUpScene()
    {
        Random.InitState(sphereSeed);

        var spheres = new List<Sphere>();

        // Add a number of random spheres
        for (int i = 0; i < maxNumSpheres; i++)
        {
            // position and radius
            Vector2 randomPos = Random.insideUnitCircle * spherePlacementRadius;

            var sphere = new Sphere { radius = sphereRadiusRange.x + Random.value * (sphereRadiusRange.y - sphereRadiusRange.x) };

            sphere.position = new Vector3(randomPos.x, sphere.radius, randomPos.y);

            // Reject spheres that are intersecting others
            foreach (var other in spheres)
            {
                float minDist = sphere.radius + other.radius;
                if (Vector3.SqrMagnitude(sphere.position - other.position) < minDist * minDist)
                    goto SkipSphere;
            }

            // Albedo and specular color
            Color color = Random.ColorHSV();
            bool metal = Random.value < 0.5f;
            sphere.albedo = metal ? Vector3.zero : new Vector3(color.r, color.g, color.b);
            sphere.specular = metal ? new Vector3(color.r, color.g, color.b) : Vector3.one * 0.04f;
            sphere.smoothness = Random.value < 0.1f ? 0.0f : Random.value;
            sphere.emission = Random.value < 0.35f ? sphere.albedo : Vector3.one * 0.05f;

            // Add the sphere to the list
            spheres.Add(sphere);

            SkipSphere:
            continue;
        }

        // Assign the compute buffer
        sphereBuffer = new ComputeBuffer(spheres.Count, 56);
        sphereBuffer.SetData(spheres);

        SetChangeBasedShaderParameters();
    }

    private void SetChangeBasedShaderParameters()
    {
        shader.SetTexture(computeKernel, "_SkyboxTexture", skyboxTexture);
        SetComputeBuffer("_Spheres", ref sphereBuffer);
        SetComputeBuffer("_MeshObjects", ref meshObjectBuffer);
        SetComputeBuffer("_Vertices", ref vertexBuffer);
        SetComputeBuffer("_Indices", ref indexBuffer);
    }
    private void SetFrequentShaderParameters()
    {
        shader.SetMatrix("_CameraToWorld", cam.cameraToWorldMatrix);
        shader.SetMatrix("_CameraInverseProjection", cam.projectionMatrix.inverse);

        shader.SetVector("_PixelOffset", new Vector2(Random.value, Random.value));
        shader.SetFloat("_Seed", Random.value);
    }

    private void Update()
    {
        if (!transform.hasChanged) return;
        currentSamples = 0;
        transform.hasChanged = false;
    }

    private void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        SetFrequentShaderParameters();
        Render(dest);
        RebuildMeshObjectBuffers();
    }

    private void Render(RenderTexture dest)
    {
        // Make sure we have a current render target
        InitRenderTexture();

        // Set the target and dispatch the compute shader
        shader.SetTexture(computeKernel, "Result", target);
        int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);

        shader.Dispatch(computeKernel, threadGroupsX, threadGroupsY, 1);

        // Blit the result texture to the screen
        if (convergeMaterial == null)
            convergeMaterial = new Material(Shader.Find("Hidden/AddShader"));
        convergeMaterial.SetFloat("_Sample", currentSamples);
        Graphics.Blit(target, converged, convergeMaterial);
        Graphics.Blit(converged, dest);
        ++currentSamples;
    }

    private void InitRenderTexture()
    {
        if (target == null || target.width != Screen.width || target.height != Screen.height)
        {
            // Release render texture if we already have one
            if (target != null)
                target.Release();

            // Get a render target ready for ray tracing
            target = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear) { enableRandomWrite = true };
            target.Create();
            currentSamples = 0;
        }

        if (converged == null || converged.width != Screen.width || converged.height != Screen.height)
        {
            // Release any existing render texture
            if (converged != null)
                converged.Release();

            // Get a render texture ready to act as our tracing buffer
            converged = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear) { enableRandomWrite = true };
            converged.Create();
        }
    }

    private void RebuildMeshObjectBuffers()
    {
        if (!meshObjectsNeedsRebuilding) return;

        meshObjectsNeedsRebuilding = false;
        currentSamples = 0;

        // clear all lists
        meshObjects.Clear();
        vertices.Clear();
        indices.Clear();

        // Loop over all objects and gather their data
        foreach (var obj in rayTracingObjects)
        {
            Mesh mesh = obj.GetComponent<MeshFilter>().sharedMesh;

            // Add vertex data
            int firstVertex = vertices.Count;
            vertices.AddRange(mesh.vertices);

            // Add index data - if the vertex buffer wasn't empty before, the indices need to be offset
            int firstIndex = indices.Count;
            var localIndices = mesh.GetIndices(0);
            indices.AddRange(localIndices.Select(index => index + firstVertex));

            // Add the object itself
            meshObjects.Add(new MeshObject
            {
                localToWorldMatrix = obj.transform.localToWorldMatrix,
                indicesOffset = firstIndex,
                indicesCount = localIndices.Length,
            });
        }

        CreateComputeBuffer(ref meshObjectBuffer, meshObjects, 72);
        CreateComputeBuffer(ref vertexBuffer, vertices, 12);
        CreateComputeBuffer(ref indexBuffer, indices, 4);

        SetChangeBasedShaderParameters();
    }

    private static void CreateComputeBuffer<T>(ref ComputeBuffer buffer, List<T> data, int stride)
        where T : struct
    {
        // Do we already have a compute buffer?
        if (buffer != null)
        {
            // If no data or buffer doesn't match the given criteria, release it
            if (data.Count == 0 || buffer.count != data.Count || buffer.stride != stride)
            {
                buffer.Release();
                buffer = null;
            }
        }

        if (data.Count != 0)
        {
            // If the buffer has been released or wasn't there to begin with, create it
            if (buffer == null)
            {
                buffer = new ComputeBuffer(data.Count, stride);
            }

            // Set data on the buffer
            buffer.SetData(data);
        }
    }

    private void SetComputeBuffer(string bufferName, ref ComputeBuffer buffer)
    {
        if (buffer != null)
        {
            shader.SetBuffer(computeKernel, bufferName, buffer);
        }
    }
}