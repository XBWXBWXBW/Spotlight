using OpenTK;
using Spotlight.EditorDrawables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace Spotlight.XBW
{
    public static class MatrixExtensions
    {
        public static Matrix4 ToMatrix4(this Matrix3 matrix3)
        {
            return new Matrix4(
                matrix3.M11, matrix3.M12, matrix3.M13, 0,
                matrix3.M21, matrix3.M22, matrix3.M23, 0,
                matrix3.M31, matrix3.M32, matrix3.M33, 0,
                0, 0, 0, 1
            );
        }

        public static float[] ToArray(this Matrix4 matrix)
        {
            return new float[]
            {
                matrix.M11, matrix.M21, matrix.M31, matrix.M41,  // 第一列
                matrix.M12, matrix.M22, matrix.M32, matrix.M42,  // 第二列
                matrix.M13, matrix.M23, matrix.M33, matrix.M43,  // 第三列
                matrix.M14, matrix.M24, matrix.M34, matrix.M44   // 第四列
            };
        }
    }

    public class ParentObject
    {
        public string objName;
        public Vector3 GlobalPosition;
        public Matrix3 GlobalRotation;
        public Vector3 GlobalScale;
    }

    public class XBW_OutputStageModel
    {
        public static Dictionary<string, float[]> shape_BufferData = new Dictionary<string, float[]>();
        public static Dictionary<string, uint[]> shape_Indices = new Dictionary<string, uint[]>();
        public static Dictionary<string, string> shape_Name = new Dictionary<string, string>();
        public static Dictionary<string, string> shape_Parent = new Dictionary<string, string>();

        // 【新增】存放每个 shapeID 对应的贴图文件名（例如: "Mario_Alb.png"）
        public static Dictionary<string, string> shape_TextureName = new Dictionary<string, string>();

        public static List<ParentObject> objList = new List<ParentObject>();

        // 辅助：根据 shapeID 生成安全名称（用于备用文件名）
        private static string idSafeName(string id) { return Regex.Replace(id, @"[\/:*?""<>|]", "_"); }
        // 导出 DAE 文件
        public static void ExportModelToDAE(string filePath)
        {
            XmlDocument doc = new XmlDocument(); XmlDeclaration xmlDeclaration = doc.CreateXmlDeclaration("1.0", "UTF-8", null); doc.AppendChild(xmlDeclaration);
            XmlElement collada = doc.CreateElement("COLLADA");
            collada.SetAttribute("xmlns", "http://www.collada.org/2005/11/COLLADASchema");
            collada.SetAttribute("version", "1.4.1");
            doc.AppendChild(collada);

            // 1. 创建资产和全局库节点
            XmlElement libraryImages = doc.CreateElement("library_images");
            XmlElement libraryMaterials = doc.CreateElement("library_materials");
            XmlElement libraryEffects = doc.CreateElement("library_effects");
            XmlElement libraryGeometries = doc.CreateElement("library_geometries");

            collada.AppendChild(libraryImages);
            collada.AppendChild(libraryMaterials);
            collada.AppendChild(libraryEffects);
            collada.AppendChild(libraryGeometries);

            // 准备导出目录
            string outputDir = System.IO.Path.GetDirectoryName(filePath) ?? ".";
            if (!System.IO.Directory.Exists(outputDir))
                System.IO.Directory.CreateDirectory(outputDir);

            // 记录已经保存过的 textureKey -> pngFileName 映射，避免重复保存
            Dictionary<string, string> savedTextureFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 2. 遍历所有网格，生成模型几何信息，并动态生成对应的材质/特效/贴图节点
            foreach (string shapeID in shape_BufferData.Keys)
            {
                shape_BufferData.TryGetValue(shapeID, out float[] bufferData);
                shape_Indices.TryGetValue(shapeID, out uint[] indices);

                // shape_TextureName 存的通常是 texture key（例如 texRef.Name），可能为空字符串
                shape_TextureName.TryGetValue(shapeID, out string textureKey);

                string textureFileName = null;

                // 如果记录了 textureKey，尝试从 BfresModelRenderer.TextureBitmaps 取出 Bitmap
                if (!string.IsNullOrEmpty(textureKey))
                {
                    //Console.WriteLine(shapeID+"  "+textureKey + "   XBW outside");

                    // BfresModelRenderer 在 Spotlight.ObjectRenderers 命名空间
                    if (Spotlight.ObjectRenderers.BfresModelRenderer.TextureBitmaps.TryGetValue(textureKey, out System.Drawing.Bitmap bmp))
                    {
                        //Console.WriteLine(shapeID + "  " + textureKey + "   XBW inside");

                        if (!savedTextureFiles.TryGetValue(textureKey, out textureFileName))
                        {
                            // 生成安全的文件名并且保证唯一性
                            string safeName = Regex.Replace(textureKey, @"[\\/:*?""<>|]", "_");
                            textureFileName = safeName + ".png";
                            string fullPath = System.IO.Path.Combine(outputDir, textureFileName);

                            // 若文件已存在且不是同一 Bitmap，可以覆盖（简单策略）
                            try
                            {
                                bmp.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
                            }
                            catch
                            {
                                // 如果直接保存失败，尝试使用一个 shape 关联名
                                textureFileName = idSafeName(shapeID) + "_" + safeName + ".png";
                                fullPath = System.IO.Path.Combine(outputDir, textureFileName);
                                bmp.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
                            }

                            savedTextureFiles[textureKey] = textureFileName;
                        }
                    }
                    else
                    {
                        // 如果 TextureBitmaps 中没有该 key，可能该 key 本身就是文件名（fallback）
                        textureFileName = textureKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || textureKey.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                            ? textureKey
                            : textureKey + ".png";
                    }
                }

                // 如果没有 texture（或未成功保存），使用默认贴图名
                if (string.IsNullOrEmpty(textureFileName))
                    textureFileName = "default_white.png";

                // 生成几何体
                XmlElement geometry = CreateGeometryElement(doc, shapeID, bufferData, indices);
                libraryGeometries.AppendChild(geometry);

                // 动态补全该模型的贴图与材质节点，传入 textureFileName（相对文件名）
                BuildMaterialNodes(doc, shapeID, textureFileName, libraryImages, libraryMaterials, libraryEffects);
            }

            // 3. 创建场景层级结构
            XmlElement libraryVisualScenes = CreateVisualSceneElement(doc);
            collada.AppendChild(libraryVisualScenes);

            // 4. 激活场景
            XmlElement scene = doc.CreateElement("scene");
            XmlElement instanceVisualScene = doc.CreateElement("instance_visual_scene");
            instanceVisualScene.SetAttribute("url", "#Scene");
            scene.AppendChild(instanceVisualScene);
            collada.AppendChild(scene);

            doc.Save(filePath);
        }

        // 动态构建 Collada 材质、特效、图像引用节点的函数
        private static void BuildMaterialNodes(XmlDocument doc, string shapeID, string textureName, XmlElement libImages, XmlElement libMaterials, XmlElement libEffects)
        {
            string imgID = shapeID + "-image";
            string matID = shapeID + "-material";
            string effID = shapeID + "-effect";

            // A. <library_images> 节点
            XmlElement image = doc.CreateElement("image");
            image.SetAttribute("id", imgID);
            image.SetAttribute("name", shapeID + "_tex");
            XmlElement initFrom = doc.CreateElement("init_from");
            initFrom.InnerText = textureName; // 相对路径文件名
            image.AppendChild(initFrom);
            libImages.AppendChild(image);

            // B. <library_materials> 节点
            XmlElement material = doc.CreateElement("material");
            material.SetAttribute("id", matID);
            material.SetAttribute("name", shapeID + "_mat");
            XmlElement instanceEffect = doc.CreateElement("instance_effect");
            instanceEffect.SetAttribute("url", "#" + effID);
            material.AppendChild(instanceEffect);
            libMaterials.AppendChild(material);

            // C. <library_effects> 节点 (标准 Blinn-Phong 材质模型渲染贴图)
            XmlElement effect = doc.CreateElement("effect");
            effect.SetAttribute("id", effID);

            XmlElement profile = doc.CreateElement("profile_COMMON");

            // 定义 Surface
            XmlElement newParamSurf = doc.CreateElement("newparam");
            newParamSurf.SetAttribute("sid", shapeID + "-surface");
            XmlElement surface = doc.CreateElement("surface");
            surface.SetAttribute("type", "2D");
            XmlElement initFromSurf = doc.CreateElement("init_from");
            initFromSurf.InnerText = imgID;
            surface.AppendChild(initFromSurf);
            newParamSurf.AppendChild(surface);
            profile.AppendChild(newParamSurf);

            // 定义 Sampler
            XmlElement newParamSamp = doc.CreateElement("newparam");
            newParamSamp.SetAttribute("sid", shapeID + "-sampler");
            XmlElement sampler = doc.CreateElement("sampler2D");
            XmlElement sourceSamp = doc.CreateElement("source");
            sourceSamp.InnerText = shapeID + "-surface";
            sampler.AppendChild(sourceSamp);
            newParamSamp.AppendChild(sampler);
            profile.AppendChild(newParamSamp);

            // 渲染技术 (Technique) 绑定纹理到 Diffuse
            XmlElement technique = doc.CreateElement("technique");
            technique.SetAttribute("sid", "common");
            XmlElement phong = doc.CreateElement("phong");
            XmlElement diffuse = doc.CreateElement("diffuse");
            XmlElement textureNode = doc.CreateElement("texture");
            textureNode.SetAttribute("texture", shapeID + "-sampler");
            textureNode.SetAttribute("texcoord", "UVSET0"); // 对应下面的 TEXCOORD 语义
            diffuse.AppendChild(textureNode);
            phong.AppendChild(diffuse);
            technique.AppendChild(phong);
            profile.AppendChild(technique);

            effect.AppendChild(profile);
            libEffects.AppendChild(effect);
        }

        // 创建模型几何信息
        private static XmlElement CreateGeometryElement(XmlDocument doc, string shapeID, float[] bufferData, uint[] indices)
        {
            XmlElement geometry = doc.CreateElement("geometry");
            geometry.SetAttribute("id", shapeID + "-geometry");

            if (shape_Name.TryGetValue(shapeID, out string realName))
            {
                geometry.SetAttribute("name", realName);
            }
            else
            {
                geometry.SetAttribute("name", shapeID);
            }

            XmlElement mesh = doc.CreateElement("mesh");
            geometry.AppendChild(mesh);

            float[] positionsData = new float[bufferData.Length / 9 * 3];
            float[] normalsData = new float[bufferData.Length / 9 * 3];
            float[] texcoordsData = new float[bufferData.Length / 9 * 2];

            for (int i = 0, j = 0; i < bufferData.Length; i += 9, j += 3)
            {
                positionsData[j] = bufferData[i];
                positionsData[j + 1] = bufferData[i + 1];
                positionsData[j + 2] = bufferData[i + 2];

                normalsData[j] = bufferData[i + 6];
                normalsData[j + 1] = bufferData[i + 7];
                normalsData[j + 2] = bufferData[i + 8];
            }

            for (int i = 3, j = 0; i < bufferData.Length; i += 9, j += 2)
            {
                texcoordsData[j] = bufferData[i];
                texcoordsData[j + 1] = 1 - bufferData[i + 1]; // 翻转 V 轴符合标准
            }

            XmlElement positions = CreateSourceElement(doc, shapeID + "-positions", positionsData, 3, "X", "Y", "Z");
            XmlElement normals = CreateSourceElement(doc, shapeID + "-normals", normalsData, 3, "X", "Y", "Z");
            XmlElement texcoords = CreateSourceElement(doc, shapeID + "-texcoords", texcoordsData, 2, "S", "T");

            mesh.AppendChild(positions);
            mesh.AppendChild(normals);
            mesh.AppendChild(texcoords);

            XmlElement vertices = doc.CreateElement("vertices");
            vertices.SetAttribute("id", shapeID + "-vertex");

            XmlElement vertexInputElement = doc.CreateElement("input");
            vertexInputElement.SetAttribute("semantic", "POSITION");
            vertexInputElement.SetAttribute("source", "#" + shapeID + "-positions");
            vertices.AppendChild(vertexInputElement);

            mesh.AppendChild(vertices);

            // --- 核心修改：让三角形网格绑定对应的材质 ID ---
            XmlElement triangles = doc.CreateElement("triangles");
            triangles.SetAttribute("count", (indices.Length / 3).ToString());
            triangles.SetAttribute("material", shapeID + "-material"); // 绑定材质
            mesh.AppendChild(triangles);

            XmlElement vertexInput = CreateInputElement(doc, "VERTEX", shapeID + "-vertex", 0);
            XmlElement normalInput = CreateInputElement(doc, "NORMAL", shapeID + "-normals", 1);
            XmlElement texcoordInput = CreateInputElement(doc, "TEXCOORD", shapeID + "-texcoords", 2);

            triangles.AppendChild(vertexInput);
            triangles.AppendChild(normalInput);
            triangles.AppendChild(texcoordInput);

            XmlElement pElement = doc.CreateElement("p");
            pElement.InnerText = string.Join(" ", indices.Select((i, index) => $"{i} {index % 3} {index % 3}"));
            triangles.AppendChild(pElement);

            return geometry;
        }

        // 创建父节点的视觉场景
        private static XmlElement CreateVisualSceneElement(XmlDocument doc)
        {
            XmlElement libraryVisualScenes = doc.CreateElement("library_visual_scenes");
            XmlElement visualScene = doc.CreateElement("visual_scene");
            visualScene.SetAttribute("id", "Scene");
            visualScene.SetAttribute("name", "Scene");

            int index = 0;
            List<string> IDList = new List<string>();
            foreach (ParentObject pObject in objList)
            {
                string parentName = pObject.objName;

                XmlElement parentNode = doc.CreateElement("node");
                parentNode.SetAttribute("id", $"{parentName}_{index:D4}");
                parentNode.SetAttribute("name", parentName);
                parentNode.SetAttribute("type", "NODE");

                XmlElement matrixElement = CreateTransformElement(doc, pObject);
                parentNode.AppendChild(matrixElement);

                visualScene.AppendChild(parentNode);

                foreach (var _spPair in shape_Parent)
                {
                    if (_spPair.Value == parentName)
                    {
                        IDList.Add(_spPair.Key);
                    }
                }

                for (int cIndex = 0; cIndex < IDList.Count; cIndex++)
                {
                    string cOriginalID = IDList[cIndex];
                    XmlElement node = doc.CreateElement("node");

                    node.SetAttribute("id", $"{cOriginalID}_{index:D4}");
                    node.SetAttribute("name", shape_Name[cOriginalID]);
                    node.SetAttribute("type", "NODE");

                    XmlElement instanceGeometry = doc.CreateElement("instance_geometry");
                    instanceGeometry.SetAttribute("url", "#" + cOriginalID + "-geometry");

                    // --- 核心修改：在物体实例化时，把网格材质映射到实际的材质定义上 ---
                    XmlElement bindMaterial = doc.CreateElement("bind_material");
                    XmlElement techniqueCommon = doc.CreateElement("technique_common");
                    XmlElement instanceMaterial = doc.CreateElement("instance_material");
                    instanceMaterial.SetAttribute("symbol", cOriginalID + "-material");
                    instanceMaterial.SetAttribute("target", "#" + cOriginalID + "-material");

                    techniqueCommon.AppendChild(instanceMaterial);
                    bindMaterial.AppendChild(techniqueCommon);
                    instanceGeometry.AppendChild(bindMaterial);
                    // -----------------------------------------------------------------

                    node.AppendChild(instanceGeometry);
                    parentNode.AppendChild(node);
                }

                IDList.Clear();
                index++;
            }

            libraryVisualScenes.AppendChild(visualScene);
            return libraryVisualScenes;
        }

        private static XmlElement CreateTransformElement(XmlDocument doc, ParentObject pObject)
        {
            XmlElement transform = doc.CreateElement("matrix");

            Matrix4 transformMatrix = pObject.GlobalRotation.ToMatrix4()
                * Matrix4.CreateScale(pObject.GlobalScale)
                * Matrix4.CreateTranslation(pObject.GlobalPosition);

            transform.InnerText = string.Join(" ", transformMatrix.ToArray().Select(f => f.ToString("0.000000")));
            return transform;
        }

        private static XmlElement CreateSourceElement(XmlDocument doc, string id, float[] data, int stride, params string[] paramNames)
        {
            XmlElement source = doc.CreateElement("source");
            source.SetAttribute("id", id);

            XmlElement floatArray = doc.CreateElement("float_array");
            floatArray.SetAttribute("id", id + "-array");
            floatArray.SetAttribute("count", data.Length.ToString());
            floatArray.InnerText = string.Join(" ", data);
            source.AppendChild(floatArray);

            XmlElement techniqueCommon = doc.CreateElement("technique_common");
            source.AppendChild(techniqueCommon);

            XmlElement accessor = doc.CreateElement("accessor");
            accessor.SetAttribute("source", "#" + id + "-array");
            accessor.SetAttribute("count", (data.Length / stride).ToString());
            accessor.SetAttribute("stride", stride.ToString());
            techniqueCommon.AppendChild(accessor);

            foreach (string param in paramNames)
            {
                XmlElement paramElement = doc.CreateElement("param");
                paramElement.SetAttribute("name", param);
                paramElement.SetAttribute("type", "float");
                accessor.AppendChild(paramElement);
            }

            return source;
        }

        private static XmlElement CreateInputElement(XmlDocument doc, string semantic, string source, int offset)
        {
            XmlElement input = doc.CreateElement("input");
            input.SetAttribute("semantic", semantic);
            input.SetAttribute("source", "#" + source);
            input.SetAttribute("offset", offset.ToString());
            return input;
        }
    }
}