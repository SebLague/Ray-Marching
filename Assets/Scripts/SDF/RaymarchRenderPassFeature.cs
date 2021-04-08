using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class RaymarchRenderPassFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class RaymarchSettings
    {
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        public ComputeShader raymarchingComputeShader;
    }

    public RaymarchSettings settings = new RaymarchSettings();

    class CustomRenderPass : ScriptableRenderPass
    {
        public RaymarchSettings settings;

        string profilerTag = "RaymarchRender";

        RenderTargetIdentifier cameraColorTexture;

        RenderTargetIdentifier tmpRT;
        int tmpRTId;

        // Raymarch stuff
        Camera cam;
        Light lightSource;
        public ComputeBuffer shapesBuffer;


        // This method is called before executing the render pass. 
        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in an performance manner.
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            //Debug.Log("Configure");
            var width = cameraTextureDescriptor.width;
            var height = cameraTextureDescriptor.height;

            tmpRTId = Shader.PropertyToID("tmpRaymarchRT");
            cmd.GetTemporaryRT(tmpRTId, width, height, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, antiAliasing: 1, enableRandomWrite: true);

            tmpRT = new RenderTargetIdentifier(tmpRTId);

            ConfigureTarget(tmpRT);

            // This will occasionally generate a 'undisposed ComputeBuffer' warning, when instances of this ScriptableRenderPass are no longer used.
            // I'd ideally like to dispose of this ComputeBuffer in FrameCleanup, but if it happens there, then the ComputeShader receives a zero-ed out buffer for some reason -
            // likely to do with the asynchronous nature of CommandBuffers.
            if (shapesBuffer != null)
            {
                shapesBuffer.Dispose();
            }
            shapesBuffer = new ComputeBuffer(10, ShapeData.GetSize());

            lightSource = GameObject.FindObjectOfType<Light>();
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref UnityEngine.Rendering.Universal.RenderingData renderingData)
        {
            if (settings.raymarchingComputeShader == null) return;

            cam = renderingData.cameraData.camera;

            cameraColorTexture = renderingData.cameraData.renderer.cameraColorTarget;
            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);

            // Copied from KawaseBlur - what does this do??
            RenderTextureDescriptor opaqueDesc = renderingData.cameraData.cameraTargetDescriptor;
            opaqueDesc.depthBufferBits = 0;

            var cs = settings.raymarchingComputeShader;

            CreateScene(cmd);
            SetParameters(cmd);

            //raymarching.SetTexture(0, "Source", source);
            cmd.SetComputeTextureParam(cs, 0, "Source", cameraColorTexture);
            //raymarching.SetTexture(0, "Destination", target);
            cmd.SetComputeTextureParam(cs, 0, "Destination", tmpRT);

            int threadGroupsX = Mathf.CeilToInt(cam.pixelWidth / 8.0f);
            int threadGroupsY = Mathf.CeilToInt(cam.pixelHeight / 8.0f);
            //raymarching.Dispatch(0, threadGroupsX, threadGroupsY, 1);
            cmd.DispatchCompute(cs, 0, threadGroupsX, threadGroupsY, 1);

            //Graphics.Blit(target, destination);
            cmd.Blit(tmpRT, cameraColorTexture);

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }

        void Init()
        {
        }

        void CreateScene(CommandBuffer cmd)
        {
            List<Shape> allShapes = new List<Shape>(GameObject.FindObjectsOfType<Shape>());
            allShapes.Sort((a, b) => a.operation.CompareTo(b.operation));

            List<Shape> orderedShapes = new List<Shape>();

            for (int i = 0; i < allShapes.Count; i++)
            {
                // Add top-level shapes (those without a parent)
                if (allShapes[i].transform.parent == null)
                {

                    Transform parentShape = allShapes[i].transform;
                    orderedShapes.Add(allShapes[i]);
                    allShapes[i].numChildren = parentShape.childCount;
                    // Add all children of the shape (nested children not supported currently)
                    for (int j = 0; j < parentShape.childCount; j++)
                    {
                        if (parentShape.GetChild(j).GetComponent<Shape>() != null)
                        {
                            orderedShapes.Add(parentShape.GetChild(j).GetComponent<Shape>());
                            orderedShapes[orderedShapes.Count - 1].numChildren = 0;
                        }
                    }
                }

            }

            //Debug.LogFormat("Found {0} shapes.", orderedShapes.Count);
            ShapeData[] shapeData = new ShapeData[orderedShapes.Count];
            for (int i = 0; i < orderedShapes.Count; i++)
            {
                var s = orderedShapes[i];
                Vector3 col = new Vector3(s.colour.r, s.colour.g, s.colour.b);
                shapeData[i] = new ShapeData() {
                    position = s.Position,
                    scale = s.Scale,
                    colour = col,
                    shapeType = (int)s.shapeType,
                    operation = (int)s.operation,
                    blendStrength = s.blendStrength * 3,
                    numChildren = s.numChildren
                };
                //Debug.LogFormat("Shape {0} is {1}.", i, shapeData[i].colour);
            }

            ComputeShader cs = settings.raymarchingComputeShader;
            //ComputeBuffer shapeBuffer = new ComputeBuffer(shapeData.Length, ShapeData.GetSize());

            //shapeBuffer.SetData(shapeData);
            cmd.SetComputeBufferData(shapesBuffer, shapeData);
            //cs.SetBuffer(0, "shapes", shapeBuffer);
            cmd.SetComputeBufferParam(cs, 0, "shapes", shapesBuffer);

            //cs.SetInt("numShapes", shapeData.Length);
            cmd.SetComputeIntParam(cs, "numShapes", shapeData.Length);
        }

        void SetParameters(CommandBuffer cmd)
        {
            bool lightIsDirectional = lightSource.type == LightType.Directional;
            ComputeShader cs = settings.raymarchingComputeShader;

            cmd.SetComputeMatrixParam(cs, "_CameraToWorld", cam.cameraToWorldMatrix);
            //cs.SetMatrix("_CameraToWorld", cam.cameraToWorldMatrix);

            cmd.SetComputeMatrixParam(cs, "_CameraInverseProjection", cam.projectionMatrix.inverse);
            //cs.SetMatrix("_CameraInverseProjection", cam.projectionMatrix.inverse);

            cmd.SetComputeVectorParam(cs, "_Light", (lightIsDirectional) ? lightSource.transform.forward : lightSource.transform.position);
            //cs.SetVector("_Light", (lightIsDirectional) ? lightSource.transform.forward : lightSource.transform.position);

            cmd.SetComputeIntParam(cs, "isDirectionalLight", lightIsDirectional ? 1 : 0);
            //cs.SetBool("positionLight", !lightIsDirectional);
        }

        [System.Serializable]
        public struct ShapeData
        {
            public Vector3 position;
            public Vector3 scale;
            public Vector3 colour;
            public int shapeType;
            public int operation;
            public float blendStrength;
            public int numChildren;

            public static int GetSize()
            {
                return sizeof(float) * 10 + sizeof(int) * 3;
            }
        }

        /// Cleanup any allocated resources that were created during the execution of this render pass.
        public override void FrameCleanup(CommandBuffer cmd)
        {
            //Debug.Log("Frame Cleanup");

            // This appears to occur too early - the buffer is zero'd out in the compute shader.
            //shapesBuffer.Dispose();
        }
    }

    CustomRenderPass m_ScriptablePass;

    public override void Create()
    {
        m_ScriptablePass = new CustomRenderPass();
        m_ScriptablePass.settings = settings;

        // Configures where the render pass should be injected.
        m_ScriptablePass.renderPassEvent = settings.renderPassEvent;
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(UnityEngine.Rendering.Universal.ScriptableRenderer renderer, ref UnityEngine.Rendering.Universal.RenderingData renderingData)
    {
        renderer.EnqueuePass(m_ScriptablePass);
    }
}