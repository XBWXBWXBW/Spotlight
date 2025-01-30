using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Spotlight.EditorDrawables;
using OpenTK;

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
    public class ParentObject {
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
        public static List<ParentObject> objList = new List<ParentObject>();

        // 导出 DAE 文件
        public static void ExportModelToDAE(string filePath)
        {
            XmlDocument doc = new XmlDocument();
            XmlDeclaration xmlDeclaration = doc.CreateXmlDeclaration("1.0", "UTF-8", null);
            doc.AppendChild(xmlDeclaration);

            XmlElement collada = doc.CreateElement("COLLADA");
            collada.SetAttribute("xmlns", "http://www.collada.org/2005/11/COLLADASchema");
            collada.SetAttribute("version", "1.4.1");
            doc.AppendChild(collada);

            XmlElement libraryGeometries = doc.CreateElement("library_geometries");
            collada.AppendChild(libraryGeometries);

            XmlElement libraryVisualScenes = CreateVisualSceneElement(doc);
            collada.AppendChild(libraryVisualScenes);

            foreach (string shapeID in shape_BufferData.Keys)
            {
                shape_BufferData.TryGetValue(shapeID, out float[] bufferData);
                shape_Indices.TryGetValue(shapeID, out uint[] indices);

                XmlElement geometry = CreateGeometryElement(doc, shapeID, bufferData, indices);
                libraryGeometries.AppendChild(geometry);
            }

            XmlElement scene = doc.CreateElement("scene");
            XmlElement instanceVisualScene = doc.CreateElement("instance_visual_scene");
            instanceVisualScene.SetAttribute("url", "#Scene");
            scene.AppendChild(instanceVisualScene);
            collada.AppendChild(scene);

            doc.Save(filePath);
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
                texcoordsData[j + 1] = 1 - bufferData[i + 1];
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

            XmlElement triangles = doc.CreateElement("triangles");
            triangles.SetAttribute("count", (indices.Length / 3).ToString());
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

                // 计算变换矩阵（位置、旋转、缩放）
                XmlElement matrixElement = CreateTransformElement(doc, pObject);
                parentNode.AppendChild(matrixElement);  // 添加matrix到父节点

                visualScene.AppendChild(parentNode);

                //添加子节点
                foreach (var _spPair in shape_Parent)
                {
                    if (_spPair.Value == parentName)
                    {
                        //把所有在parentName下的模型的ID给收集起来
                        IDList.Add(_spPair.Key);
                    }
                }

                for (int cIndex = 0; cIndex < IDList.Count; cIndex++)
                {
                    string cOriginalID = IDList[cIndex];
                    XmlElement node = doc.CreateElement("node");

                    //为每个子节点添加一个新的id，基于父节点id和子节点id生成唯一id
                    node.SetAttribute("id", $"{cOriginalID}_{index:D4}");
                    node.SetAttribute("name", shape_Name[cOriginalID]);
                    node.SetAttribute("type", "NODE");

                    XmlElement instanceGeometry = doc.CreateElement("instance_geometry");
                    //url是对应几何体的id
                    instanceGeometry.SetAttribute("url", "#" + cOriginalID + "-geometry");
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

            // 生成变换矩阵，组合了位置、旋转和缩放
            Matrix4 transformMatrix = pObject.GlobalRotation.ToMatrix4()
                * Matrix4.CreateScale(pObject.GlobalScale)
                * Matrix4.CreateTranslation(pObject.GlobalPosition);

            // 将矩阵转为字符串并设置为文本内容
            transform.InnerText = string.Join(" ", transformMatrix.ToArray().Select(f => f.ToString("0.000000")));
            return transform;
        }


        // 创建一个XML元素并返回
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

        // 创建输入元素，用于 mesh 中的 input 部分
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
