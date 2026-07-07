using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FileFormats3DW;
using GL_EditorFramework;
using GL_EditorFramework.GL_Core;
using GL_EditorFramework.Interfaces;
using OpenTK.Graphics.OpenGL;
using OpenTK;
using BfresLibrary;
using BfresLibrary.Switch;
using BfresLibrary.GX2;
using BfresLibrary.Helpers;
using Syroot.BinaryData;
using Syroot.NintenTools.NSW.Bntx.GFX;

using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using System.ComponentModel;
using SharpGLTF.Materials;
using SZS;

using Spotlight.XBW;
using System.Text.RegularExpressions;

namespace Spotlight.ObjectRenderers
{
    public static class BfresModelRenderer
    {
        private static bool initialized = false;

        public static ShaderProgram BfresShaderProgram;
        
        private static readonly Dictionary<string, CachedModel> cache = new Dictionary<string, CachedModel>();

        private static readonly Dictionary<string, Dictionary<string, int>> texArcCache = new Dictionary<string, Dictionary<string, int>>();

        public static int DefaultTetxure;

        public static int NoTetxure;

        // 新增：存放已解码/转换为 System.Drawing.Bitmap 的纹理，key = 纹理名（bfres 中的 key）
        public static readonly Dictionary<string, System.Drawing.Bitmap> TextureBitmaps = new Dictionary<string, System.Drawing.Bitmap>();

        public static void Initialize()
        {
            if (initialized)
                return;

            #region Shader Generation
            BfresShaderProgram = new ShaderProgram(
                    new FragmentShader(
                  @"#version 330
                    uniform sampler2D tex;
                    uniform vec4 highlight_color;
                    in vec2 fragUV;
                    in vec4 fragColor;
                    in vec3 fragNrm;
                    uniform vec3 light_color = vec3(1.0,1.0,1.0);

                    void main(){
                        float hc_a   = highlight_color.w;
                        vec4 color = fragColor * texture(tex,fragUV);
                        float light = 0.5;
                        light += clamp(dot(fragNrm, vec3(0,1,0)), 0.0, 1.0);

                        light = pow(light, 0.5);

                        gl_FragColor = vec4(mix(color.rgb * light, highlight_color.rgb, hc_a), color.a);
                    }"), new VertexShader(
                  @"#version 330
                    layout(location = 0) in vec4 position;
                    layout(location = 1) in vec2 uv;
                    layout(location = 2) in vec4 color;
                    layout(location = 3) in vec3 nrm;
                    uniform mat4 mtxMdl;
                    uniform mat4 mtxCam;
                    out vec2 fragUV;
                    out vec4 fragColor;
                    out vec3 fragNrm;

                    void main(){
                        fragUV = uv;
                        fragColor = color;
                        fragNrm = normalize(mat3(mtxMdl) * nrm);
                        gl_Position = mtxCam*mtxMdl*position;
                    }"));
            #endregion


            DefaultTetxure = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, DefaultTetxure);

            var bmp = Properties.Resources.DefaultTexture;
            var bmpData = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, 32, 32),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                bmp.PixelFormat);

            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, 32, 32, 0, PixelFormat.Bgr, PixelType.UnsignedByte, bmpData.Scan0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            NoTetxure = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, NoTetxure);

            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, 1, 1, 0, PixelFormat.Bgr, PixelType.UnsignedByte, new uint[] { 0xFFFFFFFF });
            bmp.UnlockBits(bmpData);

            initialized = true;
        }

        public static void Submit(string modelName, Stream stream, string textureArc = null)
        {
            //try
            //{
            ResFile bfres = new ResFile(stream);

            if (!cache.ContainsKey(modelName) && bfres.Models.Count > 0) {
                    cache[modelName] = new CachedModel(bfres, textureArc, modelName);
            }
            //}
            //catch (Exception e) 
            //{
            //    Console.WriteLine($"Error with {modelName}: {e.Message}");
            //}
        }

        public static bool TryGetModel(string modelName, out CachedModel cachedModel)
        {
            return cache.TryGetValue(modelName, out cachedModel);
        }

        public static bool TryDraw(string modelName, GL_ControlModern control, Pass pass, Vector4 highlightColor)
        {
            
            if (cache.TryGetValue(modelName, out CachedModel cachedModel))
            {
                cachedModel.Draw(control, pass, highlightColor);
                return true;
            }
            else
                return false;
        }



        public static bool Contains(string modelName) => cache.ContainsKey(modelName);

        public static void ReloadModel(string ModelName)
        {
            if (cache.ContainsKey(ModelName))
            {
                cache.Remove(ModelName);
                Submit(ModelName, new MemoryStream(SARCExt.SARC.UnpackRamN(new MemoryStream(YAZ0.Decompress(Program.TryGetPathViaProject("ObjectData", ModelName+".szs")))).Files[ModelName+".bfres"]));
            }
        }

        public sealed class CachedModel
        {
            static readonly float white = BitConverter.ToSingle(new byte[] {255, 255, 255, 255},0);
            public string xbw_Name = "";//XBW
            readonly VertexArrayObject[] vaos;
            readonly int[] indexBufferLengths;
            readonly int[] textures;
            readonly (int,int)[] wrapModes;
            readonly Pass[] passes;

            public CachedModel(ResFile bfres, string textureArc, string _xbw_Name)//XBW
            {
                xbw_Name = _xbw_Name;
                bool loadTextures = !Properties.Settings.Default.DoNotLoadTextures;

                if (loadTextures && textureArc != null && File.Exists(Program.TryGetPathViaProject("ObjectData", textureArc + ".szs")) /*&& textureArc != "SingleModeBossSharedTextures" && textureArc != "SingleModeSharedTextures"*/)
                {
                        if (!texArcCache.ContainsKey(textureArc))
                        {
                            SARCExt.SarcData objArc = SARCExt.SARC.UnpackRamN(YAZ0.Decompress(Program.TryGetPathViaProject("ObjectData", textureArc + ".szs")));

                            Dictionary<string, int> arc = new Dictionary<string, int>();
                            texArcCache.Add(textureArc, arc);
                            foreach (KeyValuePair<string, TextureShared> textureEntry in new ResFile(new MemoryStream(objArc.Files[textureArc + ".bfres"])).Textures)
                            {
                                arc.Add(textureEntry.Key, UploadTexture(textureEntry.Value, textureEntry.Key));
                            }
                        }
                }
                else if(loadTextures && textureArc != null)
                {
                    var filePath = Program.TryGetPathViaProject("ObjectData", textureArc);
                    if(Directory.Exists(filePath))
                    {
                        if (!texArcCache.ContainsKey(textureArc))
                        {
                            Dictionary<string, int> arc = new Dictionary<string, int>();
                            var filePaths = Directory.GetFiles(filePath);
                            texArcCache.Add(textureArc, arc);
                            foreach (string fileName in filePaths)
                            {

                                var image = new System.Drawing.Bitmap(fileName);
                                int texID = GL.GenTexture();

                                GL.BindTexture(TextureTarget.Texture2D, texID);
                                System.Drawing.Imaging.BitmapData data = image.LockBits(new System.Drawing.Rectangle(0, 0, image.Width, image.Height),
                                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, data.Width, data.Height, 0,
                                    OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);

                                image.UnlockBits(data);

                                // 把从文件加载的 Bitmap 保存到全局字典，键使用不带后缀的文件名
                                try
                                {
                                    string key = System.IO.Path.GetFileNameWithoutExtension(fileName);
                                    var bmpCopy = new System.Drawing.Bitmap(fileName); // 新建一个副本，避免后续 Dispose 问题
                                    lock (TextureBitmaps)
                                    {
                                        if (TextureBitmaps.ContainsKey(key))
                                        {
                                            try { TextureBitmaps[key].Dispose(); } catch { }
                                            TextureBitmaps[key] = bmpCopy;
                                        }
                                        else
                                            TextureBitmaps.Add(key, bmpCopy);
                                    }
                                    Console.WriteLine($"[BfresModelRenderer] Disk texture cached: {key} -> TextureBitmaps");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("[BfresModelRenderer] Failed to cache disk texture: " + ex.Message);
                                }

                                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

                                arc.Add(System.IO.Path.GetFileNameWithoutExtension(fileName), texID);

                                image.Dispose();
                            }
                        }
                        
                    }
                    
                }
                
                Model mdl = bfres.Models[0];

                vaos = new VertexArrayObject[mdl.Shapes.Count];
                indexBufferLengths = new int[mdl.Shapes.Count];
                textures           = new int[mdl.Shapes.Count];
                wrapModes =    new (int,int)[mdl.Shapes.Count];
                passes = new Pass[mdl.Shapes.Count];
                
                int shapeIndex = 0;
                foreach (Shape shape in mdl.Shapes.Values)
                {
                    uint[] indices = shape.Meshes[0].GetIndices().ToArray();

        // --- 新增：在每个 shape 循环开始时初始化 assignedTextureName ---
                    string assignedTextureName = null;

#pragma warning disable CS0162 // Unreachable code detected

                    if (loadTextures)
                    {
                        if (mdl.Materials[shape.MaterialIndex].TextureRefs.Count != 0)
                        {
                            int Target = 0;
                            for (int i = 0; i < mdl.Materials[shape.MaterialIndex].TextureRefs.Count; i++)
                            {
                                if (mdl.Name.Equals("CheckpointFlag") && mdl.Materials[shape.MaterialIndex].TextureRefs[i].Name.Contains(".0"))
                                {
                                    switch (Properties.Settings.Default.PlayerChoice)
                                    {
                                        case "Mario":
                                            if (bfres.Textures.ContainsKey("CheckpointFlagMark_alb.1"))
                                                mdl.Materials[shape.MaterialIndex].TextureRefs[i].Texture = bfres.Textures["CheckpointFlagMark_alb.1"];
                                            break;
                                        case "Luigi":
                                            if (bfres.Textures.ContainsKey("CheckpointFlagMark_alb.2"))
                                                mdl.Materials[shape.MaterialIndex].TextureRefs[i].Texture = bfres.Textures["CheckpointFlagMark_alb.2"];
                                            break;
                                        case "Peach":
                                            if (bfres.Textures.ContainsKey("CheckpointFlagMark_alb.3"))
                                                mdl.Materials[shape.MaterialIndex].TextureRefs[i].Texture = bfres.Textures["CheckpointFlagMark_alb.3"];
                                            break;
                                        case "Toad":
                                            if (bfres.Textures.ContainsKey("CheckpointFlagMark_alb.4"))
                                                mdl.Materials[shape.MaterialIndex].TextureRefs[i].Texture = bfres.Textures["CheckpointFlagMark_alb.4"];
                                            break;
                                        case "Rosalina":
                                            if (bfres.Textures.ContainsKey("CheckpointFlagMark_alb.5"))
                                                mdl.Materials[shape.MaterialIndex].TextureRefs[i].Texture = bfres.Textures["CheckpointFlagMark_alb.5"];
                                            break;
                                        default:
                                            break;
                                    }
                                    Target = i;
                                    break;
                                }
                                else if (mdl.Materials[shape.MaterialIndex].TextureRefs[i].Name.Contains("_alb"))
                                {
                                    Target = i;
                                    break;
                                }
                                else if (mdl.Materials[shape.MaterialIndex].Samplers[i].Name == "_a0")
                                {
                                    Target = i;
                                }
                            }
                            TextureRef texRef = mdl.Materials[shape.MaterialIndex].TextureRefs[Target];

                            TexSampler sampler = mdl.Materials[shape.MaterialIndex].Samplers[Target].TexSampler;

                            TextureShared texture = texRef.Texture;

                            if (texture == null)
                                bfres.Textures.TryGetValue(texRef.Name, out texture);

                            if (texture != null)
                            {
                                // 记录当前 shape 的纹理 key（用于后续导出）
                                assignedTextureName = texRef.Name;
                                textures[shapeIndex] = UploadTexture(texture, texRef.Name);



                            }
                            else if (textureArc != null)
                            {
                                if (texArcCache.ContainsKey(textureArc) && texArcCache[textureArc].ContainsKey(texRef.Name))
                                {
                                    textures[shapeIndex] = texArcCache[textureArc][texRef.Name];
                                    assignedTextureName = texRef.Name;
                                }
                                else
                                {
                                    textures[shapeIndex] = -2;
                                }
                            }

                            wrapModes[shapeIndex] = ((int)GetWrapMode(sampler.ClampX),
                                    (int)GetWrapMode(sampler.ClampY));
                        }
                        else
                        {
                            textures[shapeIndex] = -1;
                        }
                    }
                    else
                        textures[shapeIndex] = -2;

#pragma warning restore CS0162 // Unreachable code detected

                    switch (mdl.Materials[shape.MaterialIndex].RenderState?.FlagsMode)
                    {
                        case RenderStateFlagsMode.AlphaMask:
                        case RenderStateFlagsMode.Translucent:
                        case RenderStateFlagsMode.Custom:
                            passes[shapeIndex] = Pass.TRANSPARENT;
                            break;
                        default:
                            passes[shapeIndex] = Pass.OPAQUE;
                            break;
                    }
                    bool use_vtx_col = true;

#if ODYSSEY
                    if(mdl.Materials[shape.MaterialIndex].ShaderAssign.ShaderOptions.TryGetValue("vtxcolor_type", out ResString resString))
                        use_vtx_col = resString.String != "-1";
#else
                    if (mdl.Materials[shape.MaterialIndex].ShaderAssign.ShaderOptions.TryGetValue("VtxColorType", out ResString resString))
                        use_vtx_col = resString.String == "0";
#endif
                    Matrix4[] transforms = GetTransforms(mdl.Skeleton.Bones.Values.ToArray());

                    //Create a buffer instance which stores all the buffer data
                    VertexBufferHelper helper = new VertexBufferHelper(mdl.VertexBuffers[shapeIndex], bfres.IsPlatformSwitch ? ByteOrder.LittleEndian : ByteOrder.BigEndian);

                    //Set each array first from the lib if exist. Then add the data all in one loop
                    Syroot.Maths.Vector4F[] vec4Positions = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4Normals = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4uv0 = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4uv1 = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4uv2 = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4c0 = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4t0 = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4b0 = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4w0 = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4i0 = new Syroot.Maths.Vector4F[0];

                    //For shape morphing
                    Syroot.Maths.Vector4F[] vec4Positions1 = new Syroot.Maths.Vector4F[0];
                    Syroot.Maths.Vector4F[] vec4Positions2 = new Syroot.Maths.Vector4F[0];

                    foreach (VertexAttrib att in mdl.VertexBuffers[shapeIndex].Attributes.Values)
                    {
                        switch (att.Name)
                        {
                            case "_p0":
                                vec4Positions = helper["_p0"].Data;
                                break;
                            case "_n0":
                                vec4Normals = helper["_n0"].Data;
                                break;
                            case "_u0":
                                vec4uv0 = helper["_u0"].Data;
                                break;
                            case "_u1":
                                vec4uv1 = helper["_u1"].Data;
                                break;
                            case "_u2":
                                vec4uv2 = helper["_u2"].Data;
                                break;
                            case "_c0": 
                                if(use_vtx_col)  
                                    vec4c0 = helper["_c0"].Data;
                                break;
                            case "_t0":
                                vec4t0 = helper["_t0"].Data;
                                break;
                            case "_b0":
                                vec4b0 = helper["_b0"].Data;
                                break;
                            case "_w0":
                                vec4w0 = helper["_w0"].Data;
                                break;
                            case "_i0":
                                vec4i0 = helper["_i0"].Data;
                                break;

                            case "_p1":
                                vec4Positions1 = helper["_p1"].Data;
                                break;
                            case "_p2":
                                vec4Positions2 = helper["_p2"].Data;
                                break;
                        }
                    }

                    indexBufferLengths[shapeIndex] = indices.Length;

                    float[] bufferData = new float[9 * vec4Positions.Length];

                    int _i = 0;
                    for (int i = 0; i < vec4Positions.Length; i++)
                    {
                        Vector3 pos = Vector3.Zero;
                        Vector3 pos1 = Vector3.Zero;
                        Vector3 pos2 = Vector3.Zero;
                        Vector3 nrm = Vector3.Zero;
                        Vector2 uv0 = Vector2.Zero;
                        Vector2 uv1 = Vector2.Zero;
                        Vector2 uv2 = Vector2.Zero;
                        List<float> boneWeights = new List<float>();
                        List<int> boneIds = new List<int>();
                        Vector4 tan = Vector4.Zero;
                        Vector4 bitan = Vector4.Zero;
                        Vector4 col = Vector4.One;

                        if (vec4Positions.Length > 0)
                            pos = new Vector3(vec4Positions[i].X, vec4Positions[i].Y, vec4Positions[i].Z);
                        if (vec4Positions1.Length > 0)
                            pos1 = new Vector3(vec4Positions1[i].X, vec4Positions1[i].Y, vec4Positions1[i].Z);
                        if (vec4Positions2.Length > 0)
                            pos2 = new Vector3(vec4Positions2[i].X, vec4Positions2[i].Y, vec4Positions2[i].Z);
                        if (vec4Normals.Length > 0)
                            nrm = new Vector3(vec4Normals[i].X, vec4Normals[i].Y, vec4Normals[i].Z);
                        if (vec4uv0.Length > 0)
                            uv0 = new Vector2(vec4uv0[i].X, vec4uv0[i].Y);
                        if (vec4uv1.Length > 0)
                            uv1 = new Vector2(vec4uv1[i].X, vec4uv1[i].Y);
                        if (vec4uv2.Length > 0)
                            uv2 = new Vector2(vec4uv2[i].X, vec4uv2[i].Y);
                        if (vec4w0.Length > 0)
                        {
                            boneWeights.Add(vec4w0[i].X);
                            boneWeights.Add(vec4w0[i].Y);
                            boneWeights.Add(vec4w0[i].Z);
                            boneWeights.Add(vec4w0[i].W);
                        }
                        if (vec4i0.Length > 0)
                        {
                            boneIds.Add((int)vec4i0[i].X);
                            boneIds.Add((int)vec4i0[i].Y);
                            boneIds.Add((int)vec4i0[i].Z);
                            boneIds.Add((int)vec4i0[i].W);

                        }

                        if (vec4t0.Length > 0)
                            tan = new Vector4(vec4t0[i].X, vec4t0[i].Y, vec4t0[i].Z, vec4t0[i].W);
                        if (vec4b0.Length > 0)
                            bitan = new Vector4(vec4b0[i].X, vec4b0[i].Y, vec4b0[i].Z, vec4b0[i].W);
                        if (vec4c0.Length > 0)
                            col = new Vector4(vec4c0[i].X, vec4c0[i].Y, vec4c0[i].Z, vec4c0[i].W);

                        if (shape.VertexSkinCount == 1)
                        {
                            int boneIndex = shape.BoneIndex;
                            if (boneIds.Count > 0)
                                boneIndex = mdl.Skeleton.MatrixToBoneList[boneIds[0]];

                            //Check if the bones are a rigid type
                            //In game it seems to not transform if they are not rigid
                            if (mdl.Skeleton.Bones[boneIndex].RigidMatrixIndex != -1)
                            {
                                Matrix4 sb = transforms[boneIndex];
                                pos = Vector3.TransformPosition(pos, sb);
                                nrm = Vector3.TransformNormal(nrm, sb);
                            }
                        }

                        if (shape.VertexSkinCount == 0)
                        {
                            int boneIndex = shape.BoneIndex;

                            Matrix4 NoBindFix = transforms[boneIndex];
                            pos = Vector3.TransformPosition(pos, NoBindFix);
                            nrm = Vector3.TransformNormal(nrm, NoBindFix);
                        }


                        //write vertex data into vertex buffer
                        bufferData[_i] = pos.X * 0.01f;
                        bufferData[_i + 1] = pos.Y * 0.01f;
                        bufferData[_i + 2] = pos.Z * 0.01f;
                        if (vec4uv0.Length>0)
                        {
                            bufferData[_i + 3] = uv0.X;
                            bufferData[_i + 4] = uv0.Y;
                        }
                        if (vec4c0.Length > 0)
                        {
                            bufferData[_i + 5] = BitConverter.ToSingle(new byte[]{
                            (byte)Math.Min(col.X * 255,255),
                            (byte)Math.Min(col.Y * 255,255),
                            (byte)Math.Min(col.Z * 255,255),
                            (byte)Math.Min(col.W * 255,255)}, 0);
                        }
                        else
                            bufferData[_i + 5] = white;

                        if (vec4Normals.Length > 0)
                        {
                            bufferData[_i + 6] = nrm.X;
                            bufferData[_i + 7] = nrm.Y;
                            bufferData[_i + 8] = nrm.Z;
                        }
                        _i += 9;
                    }
                    int[] buffers = new int[2];
                    GL.GenBuffers(2, buffers);

                    int indexBuffer = buffers[0];
                    int vaoBuffer = buffers[1];

                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

                    GL.BindBuffer(BufferTarget.ArrayBuffer, vaoBuffer);
                    GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * bufferData.Length, bufferData, BufferUsageHint.StaticDraw);

                    vaos[shapeIndex] = new VertexArrayObject(vaoBuffer, indexBuffer);
                    vaos[shapeIndex].AddAttribute(0, 3, VertexAttribPointerType.Float, false, sizeof(float) * 9, 0);
                    vaos[shapeIndex].AddAttribute(1, 2, VertexAttribPointerType.Float, false, sizeof(float) * 9, sizeof(float) * 3);
                    vaos[shapeIndex].AddAttribute(2, 4, VertexAttribPointerType.UnsignedByte, true, sizeof(float) * 9, sizeof(float) * 5);
                    vaos[shapeIndex].AddAttribute(3, 3, VertexAttribPointerType.Float, false, sizeof(float) * 9, sizeof(float) * 6);

                    vaos[shapeIndex].Submit();

                    shapeIndex++;

                    //XBW
                    //这里是遍历大模型中的所有的shape，把shape的bufferData和Indices保存下来，
                    //要导出模型，只需要这个bufferData和Indices即可
                    float[] _bufferData = new float[9 * vec4Positions.Length];
                    bufferData.CopyTo(_bufferData, 0);
                    string id = mdl.Name + "_" + shape.Name;
                    XBW_OutputStageModel.shape_BufferData.Add(id, _bufferData);
                    XBW_OutputStageModel.shape_Indices.Add(id, shape.Meshes[0].GetIndices().ToArray());
                    XBW_OutputStageModel.shape_Name.Add(id, shape.Name);
                    XBW_OutputStageModel.shape_Parent.Add(id, mdl.Name);

                    // --- 关键补丁：记录 shape 对应的 texture key（可能为 null/空） ---
                    XBW_OutputStageModel.shape_TextureName[id] = assignedTextureName ?? string.Empty;
                }
            }

            static Matrix4[] GetTransforms(Bone[] bones)
            {
                Matrix4[] ret = new Matrix4[bones.Count()];
                for (int i = 0; i < bones.Length; i++)
                    ret[i] = GetBoneWorldMatrix(bones, bones[i]);

                return ret;
            }

            static Matrix4 GetBoneWorldMatrix(Bone[] bones, Bone bone)
            {
                if (bone.ParentIndex == -1)
                    return GetBoneMatrix(bone);
                else
                    return GetBoneMatrix(bone) * GetBoneWorldMatrix(bones, bones[bone.ParentIndex]);
            }

            static Matrix4 GetBoneMatrix(Bone bone)
            {
                Quaternion rotation = Quaternion.Identity;
                if (bone.FlagsRotation.HasFlag(BoneFlagsRotation.EulerXYZ))
                {
                    Quaternion xRotation = Quaternion.FromAxisAngle(Vector3.UnitX, bone.Rotation.X);
                    Quaternion yRotation = Quaternion.FromAxisAngle(Vector3.UnitY, bone.Rotation.Y);
                    Quaternion zRotation = Quaternion.FromAxisAngle(Vector3.UnitZ, bone.Rotation.Z);
                    rotation = (zRotation * yRotation * xRotation);
                }
                else
                    rotation = new Quaternion(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W);

                return Matrix4.CreateScale(new Vector3(bone.Scale.X, bone.Scale.Y, bone.Scale.Z)) *
                       Matrix4.CreateFromQuaternion(rotation) *
                       Matrix4.CreateTranslation(new Vector3(bone.Position.X, bone.Position.Y, bone.Position.Z));
            }

            public void Draw(GL_ControlModern control, Pass pass, Vector4 highlightColor)
            {
#region model rendering prepare
                switch (pass)
                {
                    case Pass.OPAQUE:
                        if (highlightColor.W != 0)
                        {
                            //prevents the highlight/outline from drawing the whole wireframe
                            GL.Enable(EnableCap.StencilTest);
                            GL.Clear(ClearBufferMask.StencilBufferBit);
                            GL.ClearStencil(0);
                            GL.StencilFunc(StencilFunction.Always, 0x1, 0x1);
                            GL.StencilOp(StencilOp.Keep, StencilOp.Replace, StencilOp.Replace);
                        }

                        control.CurrentShader = BfresShaderProgram;
                        control.CurrentShader.SetVector4("highlight_color", highlightColor);

                        GL.ActiveTexture(TextureUnit.Texture0);
                        break;

                    case Pass.TRANSPARENT:
                        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                        GL.Enable(EnableCap.Blend);

                        control.CurrentShader = BfresShaderProgram;
                        control.CurrentShader.SetVector4("highlight_color", highlightColor);

                        GL.ActiveTexture(TextureUnit.Texture0);
                        break;

                    case Pass.PICKING:
                        control.CurrentShader = Framework.SolidColorShaderProgram;
                        control.CurrentShader.SetVector4("color", control.NextPickingColor());
                        break;
                }
#endregion

                for (int i = 0; i<vaos.Length; i++)
                {
                    if (pass == Pass.PICKING)
                    {
                        vaos[i].Use(control);

                        GL.DrawElements(BeginMode.Triangles, indexBufferLengths[i], DrawElementsType.UnsignedInt, 0);
                    }
                    else
                    {
                        if (pass == passes[i]) //is up for rendering
                        {
                            //prepare for textured drawing
                            if (textures[i] == -1)
                                GL.BindTexture(TextureTarget.Texture2D, NoTetxure);
                            else if (textures[i] == -2)
                                GL.BindTexture(TextureTarget.Texture2D, DefaultTetxure);
                            else
                                GL.BindTexture(TextureTarget.Texture2D, textures[i]);

                            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrapModes[i].Item1);
                            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrapModes[i].Item2);

                            vaos[i].Use(control);

                            GL.DrawElements(BeginMode.Triangles, indexBufferLengths[i], DrawElementsType.UnsignedInt, 0);
                        }
                        else if (pass == Pass.OPAQUE && highlightColor.W != 0) //still needs to render (but invisible) for the outline effect to work
                        {
                            GL.ColorMask(false, false, false, false);
                            GL.DepthMask(false);

                            vaos[i].Use(control);

                            GL.DrawElements(BeginMode.Triangles, indexBufferLengths[i], DrawElementsType.UnsignedInt, 0);

                            GL.ColorMask(true, true, true, true);
                            GL.DepthMask(true);
                        }
                    }
                }
                GL.Disable(EnableCap.Blend);


                //Draw highlight/outline
                if (pass == Pass.OPAQUE && highlightColor.W != 0)
                {
                    control.CurrentShader = Framework.SolidColorShaderProgram;
                    control.CurrentShader.SetVector4("color", new Vector4(highlightColor.Xyz, 1));

                    GL.LineWidth(3.0f);
                    GL.StencilFunc(StencilFunction.Equal, 0x0, 0x1);
                    GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);

                    //GL.DepthFunc(DepthFunction.Always);

                    GL.PolygonMode(MaterialFace.Front, PolygonMode.Line);

                    
                    for (int i = 0; i < vaos.Length; i++)
                    {
                        vaos[i].Use(control);
                        GL.DrawElements(BeginMode.Triangles, indexBufferLengths[i], DrawElementsType.UnsignedInt, 0);
                    }
                    

                    GL.PolygonMode(MaterialFace.Front, PolygonMode.Fill);

                    //GL.DepthFunc(DepthFunction.Lequal);

                    GL.Disable(EnableCap.StencilTest);
                    GL.LineWidth(2);
                }
            }

            public void BatchDrawOpaque(GL_ControlModern control, bool drawHighlight)
            {
                for (int i = 0; i < vaos.Length; i++)
                {
                    if (passes[i] == Pass.OPAQUE)
                    {
                        if (textures[i] == -1)
                            GL.BindTexture(TextureTarget.Texture2D, NoTetxure);
                        else if (textures[i] == -2)
                            GL.BindTexture(TextureTarget.Texture2D, DefaultTetxure);
                        else
                            GL.BindTexture(TextureTarget.Texture2D, textures[i]);

                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrapModes[i].Item1);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrapModes[i].Item2);

                        vaos[i].Use(control);

                        GL.DrawElements(BeginMode.Triangles, indexBufferLengths[i], DrawElementsType.UnsignedInt, 0);
                    }
                    else if (drawHighlight)
                    {
                        GL.ColorMask(false, false, false, false);
                        GL.DepthMask(false);

                        vaos[i].Use(control);

                        GL.DrawElements(BeginMode.Triangles, indexBufferLengths[i], DrawElementsType.UnsignedInt, 0);

                        GL.ColorMask(true, true, true, true);
                        GL.DepthMask(true);
                    }
                }
            }

            public void BatchDrawTranparent(GL_ControlModern control)
            {
                for (int i = 0; i < vaos.Length; i++)
                {
                    if (passes[i] == Pass.TRANSPARENT)
                    {
                        if (textures[i] == -1)
                            GL.BindTexture(TextureTarget.Texture2D, NoTetxure);
                        else if (textures[i] == -2)
                            GL.BindTexture(TextureTarget.Texture2D, DefaultTetxure);
                        else
                            GL.BindTexture(TextureTarget.Texture2D, textures[i]);

                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrapModes[i].Item1);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrapModes[i].Item2);

                        vaos[i].Use(control);

                        GL.DrawElements(BeginMode.Triangles, indexBufferLengths[i], DrawElementsType.UnsignedInt, 0);
                    }
                }
            }

            public void BatchDrawSolidColor(GL_ControlModern control)
            {
                for (int i = 0; i < vaos.Length; i++)
                {
                    vaos[i].Use(control);
                    GL.DrawElements(BeginMode.Triangles, indexBufferLengths[i], DrawElementsType.UnsignedInt, 0);
                }
            }

            public MeshBuilder<VertexPosition, VertexColor1Texture1> VaosToMesh(GLControl control, MaterialBuilder material)
            {

                VertexBuilder<VertexPosition, VertexColor1Texture1, VertexEmpty> Vertex(float[] data)
                {
                    byte[] color = BitConverter.GetBytes(data[5]);

                    return new VertexBuilder<VertexPosition, VertexColor1Texture1, VertexEmpty>(
                        new VertexPosition(data[0], data[1], data[2]),
                        new VertexColor1Texture1(
                            new System.Numerics.Vector4(color[0] / 255f, color[1] / 255f, color[2] / 255f, color[3] / 255f),
                            new System.Numerics.Vector2(data[3], data[4])
                            ));
                }

                var builder = new MeshBuilder<VertexPosition, VertexColor1Texture1>();
                for (int i = 0; i < vaos.Length; i++)
                {
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vaos[i].buffer);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, vaos[i].indexBuffer.Value);

                    int indexCount = indexBufferLengths[i];

                    int[] indices = new int[indexCount];

                    GL.GetBufferSubData(BufferTarget.ElementArrayBuffer, new IntPtr(), indexCount * sizeof(float), indices);

                    var primitive = builder.UsePrimitive(material);

                    float[] dataA = new float[6];
                    float[] dataB = new float[6];
                    float[] dataC = new float[6];

                    for (int j = 0; j < indexCount; j+=3)
                    {
                        GL.GetBufferSubData(BufferTarget.ArrayBuffer, new IntPtr(sizeof(float) * 6 * indices[j]), sizeof(float) * 6, dataA);
                        GL.GetBufferSubData(BufferTarget.ArrayBuffer, new IntPtr(sizeof(float) * 6 * indices[j+1]), sizeof(float) * 6, dataB);
                        GL.GetBufferSubData(BufferTarget.ArrayBuffer, new IntPtr(sizeof(float) * 6 * indices[j+2]), sizeof(float) * 6, dataC);

                        primitive.AddTriangle(Vertex(dataA), Vertex(dataB), Vertex(dataC));
                    }
                }

                return builder;
            }
        }

        private static void GetPixelFormats(GX2SurfaceFormat Format, out PixelInternalFormat pixelInternalFormat)
        {
            switch (Format)
            {
                case GX2SurfaceFormat.T_BC1_UNorm:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt1Ext;
                    break;
                case GX2SurfaceFormat.T_BC1_SRGB:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt1Ext;
                    break;
                case GX2SurfaceFormat.T_BC2_UNorm:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt3Ext;
                    break;
                case GX2SurfaceFormat.T_BC2_SRGB:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt3Ext;
                    break;
                case GX2SurfaceFormat.T_BC3_UNorm:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt5Ext;
                    break;
                case GX2SurfaceFormat.T_BC3_SRGB:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt5Ext;
                    break;
                case GX2SurfaceFormat.T_BC4_UNorm:
                    pixelInternalFormat = PixelInternalFormat.CompressedRedRgtc1;
                    break;
                case GX2SurfaceFormat.T_BC4_SNorm:
                    pixelInternalFormat = PixelInternalFormat.CompressedSignedRedRgtc1;
                    break;
                case GX2SurfaceFormat.T_BC5_UNorm:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgRgtc2;
                    break;
                case GX2SurfaceFormat.T_BC5_SNorm:
                    pixelInternalFormat = PixelInternalFormat.CompressedSignedRgRgtc2;
                    break;
                case GX2SurfaceFormat.TCS_R8_G8_B8_A8_UNorm:
                    pixelInternalFormat = PixelInternalFormat.Rgba;
                    break;
                case GX2SurfaceFormat.TCS_R8_G8_B8_A8_SRGB:
                    pixelInternalFormat = PixelInternalFormat.Rgba;
                    break;
                default:
                    pixelInternalFormat = PixelInternalFormat.Rgba;
                    break;
            }
        }

        private static void GetPixelFormats(SurfaceFormat Format, out PixelInternalFormat pixelInternalFormat)
        {
            switch (Format)
            {
                case SurfaceFormat.BC1_UNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt1Ext;
                    break;
                case SurfaceFormat.BC1_SRGB:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt1Ext;
                    break;
                case SurfaceFormat.BC2_UNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt3Ext;
                    break;
                case SurfaceFormat.BC2_SRGB:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt3Ext;
                    break;
                case SurfaceFormat.BC3_UNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt5Ext;
                    break;
                case SurfaceFormat.BC3_SRGB:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaS3tcDxt5Ext;
                    break;
                case SurfaceFormat.BC4_UNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedRedRgtc1;
                    break;
                case SurfaceFormat.BC4_SNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedSignedRedRgtc1;
                    break;
                case SurfaceFormat.BC5_UNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgRgtc2;
                    break;
                case SurfaceFormat.BC5_SNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedSignedRgRgtc2;
                    break;
                case SurfaceFormat.BC6_UFLOAT:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbBptcUnsignedFloat;
                    break;
                case SurfaceFormat.BC6_FLOAT:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbBptcSignedFloat;
                    break;
                case SurfaceFormat.BC7_UNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgRgtc2;
                    break;
                case SurfaceFormat.BC7_SRGB:
                    pixelInternalFormat = PixelInternalFormat.CompressedSrgbAlphaBptcUnorm;
                    break;
                case SurfaceFormat.R8_G8_B8_A8_UNORM:
                    pixelInternalFormat = PixelInternalFormat.CompressedRgbaBptcUnorm;
                    break;
                case SurfaceFormat.R8_G8_B8_A8_SRGB:
                    pixelInternalFormat = PixelInternalFormat.Rgba;
                    break;
                default:
                    pixelInternalFormat = 0;
                    break;
            }
        }

        private static All GetChannelSwap(GX2CompSel compSel)
        {
            switch (compSel)
            {
                case GX2CompSel.ChannelR:
                    return All.Red;
                case GX2CompSel.ChannelG:
                    return All.Green;
                case GX2CompSel.ChannelB:
                    return All.Blue;
                case GX2CompSel.ChannelA:
                    return All.Alpha;
                case GX2CompSel.Always0:
                    return All.Zero;
                case GX2CompSel.Always1:
                    return All.One;
                default:
                    return All.Zero;
            }
        }

        private static All GetChannelSwap(ChannelType type)
        {
            switch (type)
            {
                case ChannelType.Red:
                    return All.Red;
                case ChannelType.Green:
                    return All.Green;
                case ChannelType.Blue:
                    return All.Blue;
                case ChannelType.Alpha:
                    return All.Alpha;
                case ChannelType.Zero:
                    return All.Zero;
                case ChannelType.One:
                    return All.One;
                default:
                    return All.Zero;
            }
        }

        private static TextureWrapMode GetWrapMode(GX2TexClamp texClamp)
        {
            switch (texClamp)
            {
                case GX2TexClamp.Clamp:
                    return TextureWrapMode.Clamp;
                case GX2TexClamp.ClampBorder:
                    return TextureWrapMode.ClampToBorder;
                case GX2TexClamp.Mirror:
                    return TextureWrapMode.MirroredRepeat;
                default:
                    return TextureWrapMode.Repeat;
            }
        }

        /// <summary>
        /// Uploads a texture to the OpenGL texture units
        /// </summary>
        /// <param name="texture">Texture to upload data from</param>
        /// <returns>Integer ID of the uploaded texture</returns>
        private static int UploadTexture(TextureShared textureShared, string textureName = null)
        {
            // Get raw (deswizzled) surface data from TextureShared
            byte[] deswizzled = textureShared.GetDeswizzledData(0, 0);

            if (deswizzled == null || deswizzled.Length == 0)
                return -2;

            PixelInternalFormat internalFormat;

            // determine platform-format and internal format mapping
            GX2SurfaceFormat? gx2Format = null;
            Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat? nsFormat = null;

            if (textureShared is BfresLibrary.WiiU.Texture textureWiiU)
            {
                gx2Format = textureWiiU.Format;
                GetPixelFormats(textureWiiU.Format, out internalFormat);
            }
            else if (textureShared is SwitchTexture textureNSW)
            {
                nsFormat = textureNSW.Format;
                GetPixelFormats(textureNSW.Format, out internalFormat);
            }
            else
                return -2;

            int tex = GL.GenTexture();
            GL.BindTexture(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, tex);

            // If not raw RGBA in GPU terms, try to decode compressed formats into RGBA for Bitmap export.
            // If decoding succeeds we will both create a System.Drawing.Bitmap and upload the decoded RGBA to GL.
            if (internalFormat != PixelInternalFormat.Rgba)
            {
                byte[] decodedRGBA = null; // expected 4 bytes per pixel (BGRA or RGBA depending on decoder, we handle below)

                try
                {
                    // WiiU (GX2) formats -> use DDSCompressor BC decoders (in FileFormats3DW)
                    if (gx2Format.HasValue)
                    {
                        switch (gx2Format.Value)
                        {
                            case GX2SurfaceFormat.T_BC1_UNorm:
                            case GX2SurfaceFormat.T_BC1_SRGB:
                                {
                                    var dec = DDSCompressor.DecompressBC1(deswizzled, (int)textureShared.Width, (int)textureShared.Height, gx2Format.Value == GX2SurfaceFormat.T_BC1_SRGB);
                                    decodedRGBA = dec.Data;
                                    break;
                                }
                            case GX2SurfaceFormat.T_BC3_UNorm:
                            case GX2SurfaceFormat.T_BC3_SRGB:
                                {
                                    var dec = DDSCompressor.DecompressBC3(deswizzled, (int)textureShared.Width, (int)textureShared.Height, gx2Format.Value == GX2SurfaceFormat.T_BC3_SRGB);
                                    decodedRGBA = dec.Data;
                                    break;
                                }
                            case GX2SurfaceFormat.T_BC5_UNorm:
                            case GX2SurfaceFormat.T_BC5_SNorm:
                                {
                                    var dec = DDSCompressor.DecompressBC5(deswizzled, (int)textureShared.Width, (int)textureShared.Height, gx2Format.Value == GX2SurfaceFormat.T_BC5_SNorm);
                                    decodedRGBA = dec.Data;
                                    break;
                                }
                            case GX2SurfaceFormat.T_BC4_UNorm:
                            case GX2SurfaceFormat.T_BC4_SNorm:
                                {
                                    var dec = DDSCompressor.DecompressBC4(deswizzled, (int)textureShared.Width, (int)textureShared.Height, gx2Format.Value == GX2SurfaceFormat.T_BC4_SNorm);
                                    decodedRGBA = dec.Data;
                                    break;
                                }
                            default:
                                decodedRGBA = null;
                                break;
                        }
                    }
                    // Nintendo Switch surface formats -> map to same decoders where applicable
                    else if (nsFormat.HasValue)
                    {
                        switch (nsFormat.Value)
                        {
                            case Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC1_UNORM:
                            case Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC1_SRGB:
                                {
                                    var dec = DDSCompressor.DecompressBC1(deswizzled, (int)textureShared.Width, (int)textureShared.Height, nsFormat.Value == Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC1_SRGB);
                                    decodedRGBA = dec.Data;
                                    break;
                                }
                            case Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC3_UNORM:
                            case Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC3_SRGB:
                                {
                                    var dec = DDSCompressor.DecompressBC3(deswizzled, (int)textureShared.Width, (int)textureShared.Height, nsFormat.Value == Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC3_SRGB);
                                    decodedRGBA = dec.Data;
                                    break;
                                }
                            case Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC5_UNORM:
                            case Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC5_SNORM:
                                {
                                    var dec = DDSCompressor.DecompressBC5(deswizzled, (int)textureShared.Width, (int)textureShared.Height, nsFormat.Value == Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC5_SNORM);
                                    decodedRGBA = dec.Data;
                                    break;
                                }
                            case Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC4_UNORM:
                            case Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC4_SNORM:
                                {
                                    var dec = DDSCompressor.DecompressBC4(deswizzled, (int)textureShared.Width, (int)textureShared.Height, nsFormat.Value == Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC4_SNORM);
                                    decodedRGBA = dec.Data;
                                    break;
                                }
                            default:
                                decodedRGBA = null;
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // decoding failed -> fall back to uploading compressed data (no bitmap)
                    Console.WriteLine($"[BfresModelRenderer] Texture decode failed for '{textureName}': {ex.Message}");
                    decodedRGBA = null;
                }

                if (decodedRGBA != null && decodedRGBA.Length >= textureShared.Width * textureShared.Height * 4)
                {
                    // DDSCompressor outputs bytes in BGRA order (see its implementation). System.Drawing.Bitmap Format32bppArgb
                    // expects memory in BGRA on Windows, so we can copy directly.
                    try
                    {
                        if (!string.IsNullOrEmpty(textureName))
                        {
                            var bmp = new System.Drawing.Bitmap((int)textureShared.Width, (int)textureShared.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                            var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);

                            System.Runtime.InteropServices.Marshal.Copy(decodedRGBA, 0, bmpData.Scan0, (int)(textureShared.Width * textureShared.Height * 4));
                            bmp.UnlockBits(bmpData);

                            lock (TextureBitmaps)
                            {
                                if (TextureBitmaps.ContainsKey(textureName))
                                {
                                    try { TextureBitmaps[textureName].Dispose(); } catch { }
                                    TextureBitmaps[textureName] = bmp;
                                }
                                else
                                    TextureBitmaps.Add(textureName, bmp);
                            }
                        }

                        // Upload decoded RGBA to GL as BGRA
                        GL.TexImage2D(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                            (int)textureShared.Width, (int)textureShared.Height, 0,
                            OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, decodedRGBA);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BfresModelRenderer] Failed to create/upload Bitmap for '{textureName}': {ex.Message}");
                        // fallback: try compressed upload below
                        decodedRGBA = null;
                    }
                }

                if (decodedRGBA == null)
                {
                    // We could not decode to RGBA or failed to create Bitmap.
                    // Upload the (already deswizzled) compressed data to GPU as compressed texture (existing behavior).
                    try
                    {
                        GL.CompressedTexImage2D(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, 0, (OpenTK.Graphics.OpenGL.InternalFormat)internalFormat,
                            (int)textureShared.Width, (int)textureShared.Height, 0, deswizzled.Length, deswizzled);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BfresModelRenderer] Compressed upload failed for '{textureName}': {ex.Message}");
                        // give up gracefully: create 1x1 'No texture' fallback
                        var blank = new byte[] { 255, 255, 255, 255 };
                        GL.TexImage2D(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 1, 1, 0, OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, blank);
                    }
                }
            }
            else
            {
                // internalFormat == Rgba: deswizzled already raw RGBA-like bytes.
                try
                {
                    if (!string.IsNullOrEmpty(textureName))
                    {
                        var bmp = new System.Drawing.Bitmap((int)textureShared.Width, (int)textureShared.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                        var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);

                        // deswizzled is expected RGBA (R,G,B,A) but GL upload used Bgra in original code.
                        // Convert RGBA -> BGRA for Bitmap memory.
                        byte[] bgra = new byte[deswizzled.Length];
                        for (int i = 0; i < deswizzled.Length; i += 4)
                        {
                            bgra[i + 0] = deswizzled[i + 2]; // B
                            bgra[i + 1] = deswizzled[i + 1]; // G
                            bgra[i + 2] = deswizzled[i + 0]; // R
                            bgra[i + 3] = deswizzled[i + 3]; // A
                        }

                        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmpData.Scan0, bgra.Length);
                        bmp.UnlockBits(bmpData);

                        lock (TextureBitmaps)
                        {
                            if (TextureBitmaps.ContainsKey(textureName))
                            {
                                try { TextureBitmaps[textureName].Dispose(); } catch { }
                                TextureBitmaps[textureName] = bmp;
                            }
                            else
                                TextureBitmaps.Add(textureName, bmp);
                        }
                    }

                    // Upload original deswizzled bytes to GL as BGRA (original behavior)
                    GL.TexImage2D(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                        (int)textureShared.Width, (int)textureShared.Height, 0,
                        OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, deswizzled);
                }
                catch
                {
                    // fallback to direct upload as RGBA if conversion/upload fails
                    GL.TexImage2D(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                        (int)textureShared.Width, (int)textureShared.Height, 0,
                        OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, deswizzled);
                }
            }

DATA_UPLOADED:

    // swizzle / channel remapping
    if (textureShared is BfresLibrary.WiiU.Texture texW)
    {
        GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureSwizzleR, (int)GetChannelSwap(texW.CompSelR));
        GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureSwizzleG, (int)GetChannelSwap(texW.CompSelG));
        GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureSwizzleB, (int)GetChannelSwap(texW.CompSelB));
        GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureSwizzleA, (int)GetChannelSwap(texW.CompSelA));
    }
    else if (textureShared is SwitchTexture texNSW)
    {
        GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureSwizzleR, (int)GetChannelSwap(texNSW.Texture.ChannelRed));
        GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureSwizzleG, (int)GetChannelSwap(texNSW.Texture.ChannelGreen));
        GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureSwizzleB, (int)GetChannelSwap(texNSW.Texture.ChannelBlue));
        GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureSwizzleA, (int)GetChannelSwap(texNSW.Texture.ChannelAlpha));
    }

    GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
    GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
    GL.TexParameter(OpenTK.Graphics.OpenGL.TextureTarget.Texture2D, OpenTK.Graphics.OpenGL.TextureParameterName.GenerateMipmap, 1);

    return tex;
        }

        // 还需要把两个调用 UploadTexture 的地方改为传入纹理名（文件中两处调用）：
        // 1) 当构建 texArcCache 时：
        //     arc.Add(textureEntry.Key, UploadTexture(textureEntry.Value, textureEntry.Key));
        // 2) 在为 shape 分配纹理时（有 texRef.Name 可用）：
        //     textures[shapeIndex] = UploadTexture(texture, texRef.Name);
    }
}
