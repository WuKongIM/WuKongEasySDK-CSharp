"""Negative cases ensure the black-box comparison fails on protocol corruption."""
import copy
import unittest

from interop import verify_exchange


class ExchangeTests(unittest.TestCase):
    def setUp(self):
        self.payload = {"type": 9001, "enabled": True, "content": "中文 🌍"}
        self.ack = {"messageId": "9223372036854775807", "messageSeq": "42",
                    "clientMsgNo": "correlation", "reasonCode": 1}
        self.message = {**self.ack, "fromUid": "sender", "payload": copy.deepcopy(self.payload)}

    def verify(self):
        verify_exchange(self.ack, self.message, "sender", self.payload, "correlation")

    def test_matching_unicode_exchange(self):
        self.verify()

    def test_server_may_omit_client_msg_no_in_ack(self):
        del self.ack["clientMsgNo"]
        self.verify()

    def test_rounded_numeric_id_is_rejected(self):
        self.ack["messageId"] = 9223372036854775808
        with self.assertRaises(AssertionError):
            self.verify()

    def test_message_id_sequence_and_correlation_must_match(self):
        for field in ("messageId", "messageSeq", "clientMsgNo", "fromUid"):
            with self.subTest(field=field):
                original = self.message[field]
                self.message[field] = "wrong"
                with self.assertRaises(AssertionError):
                    self.verify()
                self.message[field] = original

    def test_rejected_send_is_not_success(self):
        self.ack["reasonCode"] = 128
        with self.assertRaises(AssertionError):
            self.verify()

    def test_json_boolean_is_not_a_numeric_one(self):
        self.message["payload"]["enabled"] = 1
        with self.assertRaises(AssertionError):
            self.verify()
