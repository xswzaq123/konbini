"""Build-time only: converts Real-ESRGAN realesr-general-x4v3.pth (BSD-3, github.com/xinntao/Real-ESRGAN)
to a fixed-shape ONNX model that Windows ML can run. Usage: python export-model.py <in.pth> <out.onnx>
Needs torch + onnx in a throwaway virtual environment; end users never run this."""
import sys, torch, torch.nn as nn, torch.nn.functional as F

TILE = 464  # 384 px tile + 40 px margin on each side, covering the 34 px receptive field (must match AiUpscaler.cs)

class SRVGGNetCompact(nn.Module):
    def __init__(self, num_feat=64, num_conv=32, upscale=4):
        super().__init__()
        self.upscale = upscale
        self.body = nn.ModuleList([nn.Conv2d(3, num_feat, 3, 1, 1), nn.PReLU(num_parameters=num_feat)])
        for _ in range(num_conv):
            self.body.append(nn.Conv2d(num_feat, num_feat, 3, 1, 1)); self.body.append(nn.PReLU(num_parameters=num_feat))
        self.body.append(nn.Conv2d(num_feat, 3 * upscale * upscale, 3, 1, 1))
        self.upsampler = nn.PixelShuffle(upscale)
    def forward(self, x):
        out = x
        for m in self.body: out = m(out)
        return self.upsampler(out) + F.interpolate(x, scale_factor=self.upscale, mode='nearest')

net = SRVGGNetCompact()
sd = torch.load(sys.argv[1], map_location='cpu')
net.load_state_dict(sd.get('params', sd)); net.eval()
torch.onnx.export(net, torch.rand(1, 3, TILE, TILE), sys.argv[2], opset_version=11,
                  input_names=['input'], output_names=['output'], dynamo=False)
print('exported', sys.argv[2])
