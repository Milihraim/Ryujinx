using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Vulkan
{
    class Shader : IDisposable
    {

        private static readonly IntPtr _ptrMainEntryPointName = Marshal.StringToHGlobalAnsi("main");

        private readonly Vk _api;
        private readonly Device _device;
        private readonly ShaderStageFlags _stage;

        private bool _disposed;
        private ShaderModule _module;

        public ShaderStageFlags StageFlags => _stage;

        public ProgramLinkStatus CompileStatus { private set; get; }

        public readonly Task CompileTask;

        public unsafe Shader(Vk api, Device device, ShaderSource shaderSource)
        {
            _api = api;
            _device = device;

            CompileStatus = ProgramLinkStatus.Incomplete;

            _stage = shaderSource.Stage.Convert();

            CompileTask = Task.Run(() =>
            {
                byte[] spirv = shaderSource.BinaryCode;

                if (spirv == null)
                {
                    spirv = GlslToSpirv(shaderSource.Code, shaderSource.Stage);

                    if (spirv == null)
                    {
                        CompileStatus = ProgramLinkStatus.Failure;

                        return;
                    }
                }

                fixed (byte* pCode = spirv)
                {
                    var shaderModuleCreateInfo = new ShaderModuleCreateInfo
                    {
                        SType = StructureType.ShaderModuleCreateInfo,
                        CodeSize = (uint)spirv.Length,
                        PCode = (uint*)pCode,
                    };

                    api.CreateShaderModule(device, shaderModuleCreateInfo, null, out _module).ThrowOnError();
                }

                CompileStatus = ProgramLinkStatus.Success;
            });
        }

        private unsafe static byte[] GlslToSpirv(string glsl, ShaderStage stage)
        {
            var api = Shaderc.GetApi();
            var compile = api.CompilerInitialize();
            var options = api.CompileOptionsInitialize();
            api.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
            api.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc15);
            api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, Vk.Version12);
            api.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            
            var shaderKind = GetShaderCShaderStage(stage);

            IntPtr glslPtr = Marshal.StringToHGlobalAnsi(glsl);
            int ansiLength = System.Text.Encoding.Default.GetByteCount(glsl);

            var scr = api.CompileIntoSpv(compile, (byte*)glslPtr, (UIntPtr)ansiLength, shaderKind, "Ryu", (byte*)_ptrMainEntryPointName, options);
            
            Marshal.FreeHGlobal(glslPtr);

            if (api.ResultGetCompilationStatus(scr) != CompilationStatus.Success)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Shader compilation error: {api.ResultGetCompilationStatus(scr)} {api.ResultGetErrorMessageS(scr)}");
                api.CompileOptionsRelease(options);
                api.CompilerRelease(compile);
                api.Dispose();

                return null;
            }

            var spirvBytes = new Span<byte>(api.ResultGetBytes(scr), (int)api.ResultGetLength(scr));

            byte[] code = new byte[((uint)api.ResultGetLength(scr) + 3) & ~3];

            spirvBytes.CopyTo(code.AsSpan()[..(int)api.ResultGetLength(scr)]);
            
            api.CompileOptionsRelease(options);
            api.CompilerRelease(compile);
            api.Dispose();
            return code;
        }

        private static ShaderKind GetShaderCShaderStage(ShaderStage stage)
        {
            switch (stage)
            {
                case ShaderStage.Vertex:
                    return ShaderKind.GlslVertexShader;
                case ShaderStage.Geometry:
                    return ShaderKind.GlslGeometryShader;
                case ShaderStage.TessellationControl:
                    return ShaderKind.GlslTessControlShader;
                case ShaderStage.TessellationEvaluation:
                    return ShaderKind.GlslTessEvaluationShader;
                case ShaderStage.Fragment:
                    return ShaderKind.GlslFragmentShader;
                case ShaderStage.Compute:
                    return ShaderKind.GlslComputeShader;
            }

            Logger.Debug?.Print(LogClass.Gpu, $"Invalid {nameof(ShaderStage)} enum value: {stage}.");

            return ShaderKind.GlslDefaultVertexShader;
        }

        public unsafe PipelineShaderStageCreateInfo GetInfo()
        {
            return new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = _stage,
                Module = _module,
                PName = (byte*)_ptrMainEntryPointName,
            };
        }

        public void WaitForCompile()
        {
            CompileTask.Wait();
        }

        public unsafe void Dispose()
        {
            if (!_disposed)
            {
                _api.DestroyShaderModule(_device, _module, null);
                _disposed = true;
            }
        }
    }
}
