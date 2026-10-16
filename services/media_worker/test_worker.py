from __future__ import annotations

import hashlib
import ssl
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import worker


class TlsTrustTests(unittest.TestCase):
    def test_sha256_pin_accepts_hex_and_separated_fingerprints(self) -> None:
        pin = bytes(range(32))
        fingerprint = pin.hex()
        separated = ":".join(fingerprint[index:index + 2] for index in range(0, len(fingerprint), 2))

        self.assertEqual(worker._parse_sha256_pin(fingerprint), pin)
        self.assertEqual(worker._parse_sha256_pin(separated), pin)

    def test_sha256_pin_rejects_invalid_fingerprints(self) -> None:
        with self.assertRaises(worker.WorkerError):
            worker._parse_sha256_pin("not-a-pin")
        with self.assertRaises(worker.WorkerError):
            worker._parse_sha256_pin("g" * 64)

    def test_leaf_certificate_must_match_exact_pin(self) -> None:
        certificate = b"NAS leaf certificate fixture"
        pin = hashlib.sha256(certificate).digest()

        self.assertTrue(worker._certificate_matches_pin(certificate, pin))
        self.assertFalse(worker._certificate_matches_pin(certificate, b"\0" * 32))
        self.assertFalse(worker._certificate_matches_pin(None, pin))

    def test_worker_ca_bundle_matches_the_desktop_trust_anchor(self) -> None:
        worker_ca = Path(__file__).with_name("nas-root-ca.pem")
        desktop_ca = Path(__file__).parents[2] / "desktop" / "MuYingDrive.UpdateCore" / "Trust" / "nas-root-ca.pem"
        self.assertEqual(worker_ca.read_bytes(), desktop_ca.read_bytes())
        self.assertEqual(hashlib.sha256(worker_ca.read_bytes()).hexdigest(), worker.BUNDLED_NAS_CA_SHA256)

    def test_ca_context_keeps_chain_and_hostname_verification_enabled(self) -> None:
        ca_file = Path(__file__).with_name("nas-root-ca.pem")
        context = worker._create_tls_context(ca_file)

        self.assertEqual(context.verify_mode, ssl.CERT_REQUIRED)
        self.assertTrue(context.check_hostname)

    def test_ca_context_rejects_a_different_certificate_before_tls_load(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            wrong_ca = Path(directory) / "wrong-ca.pem"
            wrong_ca.write_text("-----BEGIN CERTIFICATE-----\nnot the NAS CA\n-----END CERTIFICATE-----\n",
                                encoding="ascii")

            with mock.patch.object(worker.ssl, "create_default_context") as create_context:
                with self.assertRaisesRegex(worker.WorkerError, "does not match the bundled NAS CA"):
                    worker._create_tls_context(wrong_ca)

            create_context.assert_not_called()


if __name__ == "__main__":
    unittest.main()
