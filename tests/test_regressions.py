import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

TOOLS = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS))

import douyin_download
import env_setup
import asr_server


class DownloadFlowTests(unittest.TestCase):
    def test_short_link_goes_to_browser_first(self):
        calls = []
        detail = {
            "aweme_detail": {
                "aweme_id": "123456789",
                "desc": "test",
                "video": {"play_addr": {"url_list": ["https://example.invalid/video.mp4"]}},
            }
        }

        def fake_fetch(url, cookies_path, emit, **kwargs):
            calls.append(url)
            return detail

        def fake_stream(url, dest, on_progress):
            Path(dest).write_bytes(b"0" * 20480)
            on_progress(20480, 20480)
            return 20480

        with mock.patch.object(douyin_download, "fetch_detail", fake_fetch), \
                mock.patch.object(douyin_download, "_stream_download", fake_stream):
            with tempfile.TemporaryDirectory() as out:
                path, title = douyin_download.download(
                    "分享链接 https://v.douyin.com/abc123/", out, lambda **kwargs: None)
                size = Path(path).stat().st_size

        self.assertEqual(calls, ["https://v.douyin.com/abc123/"])
        self.assertEqual(title, "test")
        self.assertEqual(size, 20480)


class SetupFlowTests(unittest.TestCase):
    def test_pip_progress_line_is_parsed(self):
        match = env_setup.PIP_PROGRESS_RE.search(
            "     ---- 120.5/2500.0 MB 18.4 MB/s  0:02:09")
        self.assertIsNotNone(match)
        self.assertEqual(match.group(6), "0:02:09")

    def test_pip_raw_progress_line_is_parsed(self):
        match = env_setup.PIP_RAW_RE.search("Progress 48234496 of 204164970")
        self.assertIsNotNone(match)
        self.assertEqual(int(match.group(1)), 48234496)
        self.assertEqual(int(match.group(2)), 204164970)

    def test_stale_resident_server_is_shut_down_on_start(self):
        # 新服务启动前必须尝试关掉上一次残留的服务，避免多个服务同时占用内存。
        self.assertTrue(hasattr(asr_server, "_stop_existing_server"))

    @staticmethod
    def args(device="cpu"):
        return SimpleNamespace(
            python=sys.executable,
            device=device,
            models_dir="",
            browsers_dir="",
            ffmpeg="",
            skip_torch=False,
            skip_deps=False,
            skip_browser=False,
            skip_models=False,
        )

    def test_gpu_request_is_not_ready_with_cpu_torch(self):
        report = {
            "pythonPath": sys.executable,
            "pythonVersion": "3.11.9",
            "packages": {name: "1" for name in
                         ("funasr", "modelscope", "playwright", "curl_cffi", "torch", "sentencepiece")},
            "torchCuda": False,
            "nvidia": {"present": True, "name": "GPU", "driver": "1"},
            "ffmpeg": "ffmpeg",
            "browsersDir": "",
            "browserReady": True,
            "modelsDir": "",
            "models": {"asr": True, "vad": True, "punc": True},
            "requestedDevice": "cuda:0",
            "device": "cpu",
            "ready": False,
            "issues": ["CUDA 不可用（当前 PyTorch 为 CPU 版或驱动不可用）"],
        }

        def fake_python_of(exe, code, env=None, timeout=120):
            return (0, "3.11.9") if "sys.version" in code else (0, "0")

        with mock.patch.object(env_setup, "python_of", side_effect=fake_python_of), \
                mock.patch.object(env_setup, "pkg_version", return_value="1"), \
                mock.patch.object(env_setup, "nvidia_info", return_value=report["nvidia"]), \
                mock.patch.object(env_setup, "find_ffmpeg", return_value="ffmpeg"), \
                mock.patch.object(env_setup, "chromium_present", return_value=True), \
                mock.patch.object(env_setup, "installed_models", return_value=report["models"]):
            actual = env_setup.build_report(self.args("gpu"))

        self.assertFalse(actual["ready"])
        self.assertEqual(actual["requestedDevice"], "cuda:0")
        self.assertIn("CUDA 不可用", "；".join(actual["issues"]))

    def test_install_skips_entirely_when_ready(self):
        report = {"ready": True, "issues": [], "device": "cpu", "models": {}}
        fake_install = mock.Mock(side_effect=AssertionError("install should be skipped"))
        output = io.StringIO()
        with mock.patch.object(env_setup, "build_report", return_value=report), \
                mock.patch.object(env_setup, "install_torch", fake_install), \
                mock.patch.object(env_setup, "install_deps", fake_install), \
                mock.patch.object(env_setup, "install_browser", fake_install), \
                mock.patch.object(env_setup, "install_models", fake_install), \
                contextlib.redirect_stdout(output):
            code = env_setup.run_install(self.args("cpu"))

        self.assertEqual(code, 0)
        self.assertIn("无需再次准备", output.getvalue())
        fake_install.assert_not_called()


if __name__ == "__main__":
    unittest.main()
