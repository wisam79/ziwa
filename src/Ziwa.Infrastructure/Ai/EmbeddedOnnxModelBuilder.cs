using System.Buffers.Binary;
using System.Text;

namespace Ziwa.Infrastructure.Ai;

public static class EmbeddedOnnxModelBuilder
{
    private const int OnnxIrVersion = 7;
    private const long OnnxOpsetVersion = 13;
    private const int TensorDataTypeFloat = 1;
    private const int AttributeTypeInts = 7;

    public static byte[] BuildForegroundSegmentationModelBytes()
    {
        // Input: [1, 5, H, W] (backdropDist, chromaDist, lumaDist, edgeEnergy, portraitPrior)
        // Conv1 (3x3, 5->8) -> Relu -> Conv2 (5x5, 8->8) -> Relu -> Conv3 (1x1, 8->1) -> Sigmoid
        float[] w1 = BuildConv3x3Weights(5, 8, (oc, ic, ky, kx) =>
        {
            float spatial = (ky == 1 && kx == 1) ? 0.36f : 0.08f;
            return (oc, ic) switch
            {
                (0, 0) => spatial * 1.45f,
                (1, 1) => spatial * 1.35f,
                (2, 2) => spatial * 1.20f,
                (3, 3) => spatial * 1.50f,
                (4, 4) => spatial * 1.30f,
                (5, 0) => spatial * 0.90f,
                (5, 4) => spatial * 0.85f,
                (6, 1) => spatial * 0.85f,
                (6, 3) => spatial * 0.80f,
                (7, 2) => spatial * 0.70f,
                (7, 4) => spatial * 0.95f,
                _ => 0f,
            };
        });
        float[] b1 = [-0.22f, -0.20f, -0.24f, -0.18f, -0.30f, -0.35f, -0.25f, -0.32f];

        float[] w2 = BuildConvNxNWeights(8, 8, 5, (oc, ic, ky, kx) =>
        {
            float dist = MathF.Abs(ky - 2) + MathF.Abs(kx - 2);
            float g = MathF.Exp(-dist * 0.45f) / 9.2f;
            if (oc == ic)
            {
                return g * 1.35f;
            }

            if ((oc + ic) % 3 == 0)
            {
                return g * 0.35f;
            }

            return 0f;
        });
        float[] b2 = [-0.05f, -0.05f, -0.05f, -0.04f, -0.06f, -0.05f, -0.05f, -0.06f];

        float[] w3 = [1.85f, 1.45f, 1.15f, 1.35f, 1.10f, 1.25f, 1.15f, 1.10f];
        float[] b3 = [-2.55f];

        var initializers = new List<byte[]>
        {
            EncodeFloatTensor("w1", [8, 5, 3, 3], w1),
            EncodeFloatTensor("b1", [8], b1),
            EncodeFloatTensor("w2", [8, 8, 5, 5], w2),
            EncodeFloatTensor("b2", [8], b2),
            EncodeFloatTensor("w3", [1, 8, 1, 1], w3),
            EncodeFloatTensor("b3", [1], b3),
        };

        var nodes = new List<byte[]>
        {
            EncodeNode("conv1", "Conv", ["input", "w1", "b1"], ["c1"], [EncodeIntsAttr("pads", [1, 1, 1, 1]), EncodeIntsAttr("strides", [1, 1])]),
            EncodeNode("relu1", "Relu", ["c1"], ["r1"], []),
            EncodeNode("conv2", "Conv", ["r1", "w2", "b2"], ["c2"], [EncodeIntsAttr("pads", [2, 2, 2, 2]), EncodeIntsAttr("strides", [1, 1])]),
            EncodeNode("relu2", "Relu", ["c2"], ["r2"], []),
            EncodeNode("conv3", "Conv", ["r2", "w3", "b3"], ["logits"], [EncodeIntsAttr("pads", [0, 0, 0, 0]), EncodeIntsAttr("strides", [1, 1])]),
            EncodeNode("sig", "Sigmoid", ["logits"], ["alpha"], []),
        };

        return EncodeModel(
            graphName: "ZiwaForegroundMatteNet",
            inputName: "input",
            inputChannels: 5,
            outputName: "alpha",
            outputChannels: 1,
            nodes: nodes,
            initializers: initializers);
    }

    public static byte[] BuildFaceDetectorModelBytes()
    {
        // Input: [1, 4, H, W] (skinAndPortrait, eyeBandContrast, bilateralSymmetry, detailEnergy)
        // Conv1 (5x5, 4->6) -> Relu -> AveragePool (3x3) -> Conv2 (1x1, 6->1) -> Sigmoid
        float[] w1 = BuildConvNxNWeights(4, 6, 5, (oc, ic, ky, kx) =>
        {
            float d = MathF.Abs(ky - 2) + MathF.Abs(kx - 2);
            float g = MathF.Exp(-d * 0.42f) / 9.8f;
            return (oc, ic) switch
            {
                (0, 0) => g * 1.65f,
                (1, 1) => g * 1.50f,
                (2, 2) => g * 1.35f,
                (3, 3) => g * 1.40f,
                (4, 0) => g * 0.90f,
                (4, 1) => g * 0.85f,
                (5, 2) => g * 0.85f,
                (5, 3) => g * 0.85f,
                _ => 0f,
            };
        });
        float[] b1 = [-0.25f, -0.18f, -0.22f, -0.14f, -0.35f, -0.30f];

        float[] w2 = [1.80f, 1.65f, 1.25f, 1.30f, 1.55f, 1.35f];
        float[] b2 = [-2.60f];

        var initializers = new List<byte[]>
        {
            EncodeFloatTensor("w1", [6, 4, 5, 5], w1),
            EncodeFloatTensor("b1", [6], b1),
            EncodeFloatTensor("w2", [1, 6, 1, 1], w2),
            EncodeFloatTensor("b2", [1], b2),
        };

        var nodes = new List<byte[]>
        {
            EncodeNode("conv1", "Conv", ["input", "w1", "b1"], ["c1"], [EncodeIntsAttr("pads", [2, 2, 2, 2]), EncodeIntsAttr("strides", [1, 1])]),
            EncodeNode("relu1", "Relu", ["c1"], ["r1"], []),
            EncodeNode("pool1", "AveragePool", ["r1"], ["p1"], [EncodeIntsAttr("kernel_shape", [3, 3]), EncodeIntsAttr("pads", [1, 1, 1, 1]), EncodeIntsAttr("strides", [1, 1])]),
            EncodeNode("conv2", "Conv", ["p1", "w2", "b2"], ["logits"], [EncodeIntsAttr("pads", [0, 0, 0, 0]), EncodeIntsAttr("strides", [1, 1])]),
            EncodeNode("sig", "Sigmoid", ["logits"], ["heatmap"], []),
        };

        return EncodeModel(
            graphName: "ZiwaFaceProposalNet",
            inputName: "input",
            inputChannels: 4,
            outputName: "heatmap",
            outputChannels: 1,
            nodes: nodes,
            initializers: initializers);
    }

    public static byte[] BuildTestExternal3ChannelSegmentationModelBytes()
    {
        // Valid [1, 3, H, W] RGB -> [1, 1, H, W] ONNX model for external-model integration tests.
        float[] w1 = [0.6f, 0.25f, 0.25f];
        float[] b1 = [-0.30f];
        var initializers = new List<byte[]>
        {
            EncodeFloatTensor("w1", [1, 3, 1, 1], w1),
            EncodeFloatTensor("b1", [1], b1),
        };

        var nodes = new List<byte[]>
        {
            EncodeNode("conv1", "Conv", ["input", "w1", "b1"], ["logits"], [EncodeIntsAttr("pads", [0, 0, 0, 0]), EncodeIntsAttr("strides", [1, 1])]),
            EncodeNode("sig", "Sigmoid", ["logits"], ["alpha"], []),
        };

        return EncodeModel(
            graphName: "ZiwaExternalTestSegNet",
            inputName: "input",
            inputChannels: 3,
            outputName: "alpha",
            outputChannels: 1,
            nodes: nodes,
            initializers: initializers);
    }

    private static float[] BuildConv3x3Weights(int inC, int outC, Func<int, int, int, int, float> kernel) =>
        BuildConvNxNWeights(inC, outC, 3, kernel);

    private static float[] BuildConvNxNWeights(int inC, int outC, int k, Func<int, int, int, int, float> kernel)
    {
        float[] weights = new float[outC * inC * k * k];
        for (int oc = 0; oc < outC; oc++)
        {
            for (int ic = 0; ic < inC; ic++)
            {
                for (int ky = 0; ky < k; ky++)
                {
                    for (int kx = 0; kx < k; kx++)
                    {
                        int idx = (((((oc * inC) + ic) * k) + ky) * k) + kx;
                        weights[idx] = kernel(oc, ic, ky, kx);
                    }
                }
            }
        }

        return weights;
    }

    private static byte[] EncodeModel(
        string graphName,
        string inputName,
        long inputChannels,
        string outputName,
        long outputChannels,
        IReadOnlyList<byte[]> nodes,
        IReadOnlyList<byte[]> initializers)
    {
        byte[] graphBytes = EncodeGraph(
            graphName,
            EncodeValueInfo(inputName, inputChannels),
            EncodeValueInfo(outputName, outputChannels),
            nodes,
            initializers);
        byte[] opsetBytes = EncodeOpsetImport(string.Empty, OnnxOpsetVersion);

        using var ms = new MemoryStream();
        WriteVarintField(ms, 1, OnnxIrVersion);
        WriteStringField(ms, 2, "ZiwaStudio");
        WriteBytesField(ms, 7, graphBytes);
        WriteBytesField(ms, 8, opsetBytes);
        return ms.ToArray();
    }

    private static byte[] EncodeGraph(
        string name,
        byte[] inputInfo,
        byte[] outputInfo,
        IReadOnlyList<byte[]> nodes,
        IReadOnlyList<byte[]> initializers)
    {
        using var ms = new MemoryStream();
        foreach (byte[] node in nodes)
        {
            WriteBytesField(ms, 1, node);
        }

        WriteStringField(ms, 2, name);
        foreach (byte[] init in initializers)
        {
            WriteBytesField(ms, 5, init);
        }

        WriteBytesField(ms, 11, inputInfo);
        WriteBytesField(ms, 12, outputInfo);
        return ms.ToArray();
    }

    private static byte[] EncodeOpsetImport(string domain, long version)
    {
        using var ms = new MemoryStream();
        if (!string.IsNullOrEmpty(domain))
        {
            WriteStringField(ms, 1, domain);
        }

        WriteVarintField(ms, 2, version);
        return ms.ToArray();
    }

    private static byte[] EncodeValueInfo(string name, long channels)
    {
        using var shape = new MemoryStream();
        WriteBytesField(shape, 1, EncodeDimValue(1));
        WriteBytesField(shape, 1, EncodeDimValue(channels));
        WriteBytesField(shape, 1, EncodeDimParam("H"));
        WriteBytesField(shape, 1, EncodeDimParam("W"));

        using var tensorType = new MemoryStream();
        WriteVarintField(tensorType, 1, TensorDataTypeFloat);
        WriteBytesField(tensorType, 2, shape.ToArray());

        using var typeProto = new MemoryStream();
        WriteBytesField(typeProto, 1, tensorType.ToArray());

        using var valueInfo = new MemoryStream();
        WriteStringField(valueInfo, 1, name);
        WriteBytesField(valueInfo, 2, typeProto.ToArray());
        return valueInfo.ToArray();
    }

    private static byte[] EncodeDimValue(long value)
    {
        using var ms = new MemoryStream();
        WriteVarintField(ms, 1, value);
        return ms.ToArray();
    }

    private static byte[] EncodeDimParam(string param)
    {
        using var ms = new MemoryStream();
        WriteStringField(ms, 2, param);
        return ms.ToArray();
    }

    private static byte[] EncodeFloatTensor(string name, long[] dims, float[] values)
    {
        byte[] raw = new byte[values.Length * sizeof(float)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * sizeof(float), sizeof(float)), values[i]);
        }

        using var ms = new MemoryStream();
        foreach (long d in dims)
        {
            WriteVarintField(ms, 1, d);
        }

        WriteVarintField(ms, 2, TensorDataTypeFloat);
        WriteStringField(ms, 8, name);
        WriteBytesField(ms, 9, raw);
        return ms.ToArray();
    }

    private static byte[] EncodeNode(
        string name,
        string opType,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string> outputs,
        IReadOnlyList<byte[]> attributes)
    {
        using var ms = new MemoryStream();
        foreach (string input in inputs)
        {
            WriteStringField(ms, 1, input);
        }

        foreach (string output in outputs)
        {
            WriteStringField(ms, 2, output);
        }

        WriteStringField(ms, 3, name);
        WriteStringField(ms, 4, opType);
        foreach (byte[] attr in attributes)
        {
            WriteBytesField(ms, 5, attr);
        }

        return ms.ToArray();
    }

    private static byte[] EncodeIntsAttr(string name, long[] ints)
    {
        using var ms = new MemoryStream();
        WriteStringField(ms, 1, name);
        foreach (long v in ints)
        {
            WriteVarintField(ms, 8, v);
        }

        WriteVarintField(ms, 20, AttributeTypeInts);
        return ms.ToArray();
    }

    private static void WriteVarintField(Stream s, int fieldNumber, long value)
    {
        WriteVarint(s, (ulong)((fieldNumber << 3) | 0));
        WriteVarint(s, unchecked((ulong)value));
    }

    private static void WriteStringField(Stream s, int fieldNumber, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteBytesField(s, fieldNumber, bytes);
    }

    private static void WriteBytesField(Stream s, int fieldNumber, byte[] data)
    {
        WriteVarint(s, (ulong)((fieldNumber << 3) | 2));
        WriteVarint(s, (ulong)data.Length);
        s.Write(data, 0, data.Length);
    }

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        s.WriteByte((byte)value);
    }
}
