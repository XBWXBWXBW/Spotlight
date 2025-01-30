using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.IO;
using Spotlight.EditorDrawables;

namespace Spotlight.XBW
{
    public class XBW_OutputStageModel
    {
        public static Dictionary<string, float[]> shape_BufferData = new Dictionary<string, float[]>();
        public static Dictionary<string, uint[]> shape_Indices = new Dictionary<string, uint[]>();
        public static Dictionary<string, string> shape_Name = new Dictionary<string, string>();
        public static Dictionary<string, string> shape_Parent = new Dictionary<string, string>();
        public static List<General3dWorldObject> objList = new List<General3dWorldObject>()

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

            XmlElement libraryVisualScenes = CreateVisualSceneElement(doc, shape_BufferData.Keys.ToList());
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

        private static XmlElement CreateGeometryElement(XmlDocument doc, string shapeID, float[] bufferData, uint[] indices)
        {
            XmlElement geometry = doc.CreateElement("geometry");
            geometry.SetAttribute("id", shapeID + "-geometry");

            // 设置 name，使用 shape_Name，如果没有则使用 shapeID
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
            pElement.InnerText = string.Join(" ", indices.SelectMany(i => Enumerable.Repeat(i, 3)));
            triangles.AppendChild(pElement);

            return geometry;
        }

        private static XmlElement CreateVisualSceneElement(XmlDocument doc, List<string> shapeNames)
        {
            XmlElement libraryVisualScenes = doc.CreateElement("library_visual_scenes");
            XmlElement visualScene = doc.CreateElement("visual_scene");
            visualScene.SetAttribute("id", "Scene");
            visualScene.SetAttribute("name", "Scene");

            // 存储已经创建的父节点
            Dictionary<string, XmlElement> parentNodes = new Dictionary<string, XmlElement>();

            foreach (string shapeID in shapeNames)
            {
                // 获取父节点的名字
                string parentName = shape_Parent.ContainsKey(shapeID) ? shape_Parent[shapeID] : "root";

                // 确保父节点存在
                if (!parentNodes.TryGetValue(parentName, out XmlElement parentNode))
                {
                    parentNode = doc.CreateElement("node");
                    parentNode.SetAttribute("id", "parent_" + parentName);
                    parentNode.SetAttribute("name", parentName);
                    parentNode.SetAttribute("type", "NODE");

                    parentNodes[parentName] = parentNode;
                    visualScene.AppendChild(parentNode); // 把父节点加入场景
                }

                // 创建模型节点
                XmlElement node = doc.CreateElement("node");
                node.SetAttribute("id", "node_" + shapeID);
                node.SetAttribute("name", shape_Name.ContainsKey(shapeID) ? shape_Name[shapeID] : shapeID);
                node.SetAttribute("type", "NODE");

                // 绑定 geometry
                XmlElement instanceGeometry = doc.CreateElement("instance_geometry");
                instanceGeometry.SetAttribute("url", "#" + shapeID + "-geometry");
                node.AppendChild(instanceGeometry);

                // 把模型节点放入对应的父节点
                parentNode.AppendChild(node);
            }

            libraryVisualScenes.AppendChild(visualScene);
            return libraryVisualScenes;
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
