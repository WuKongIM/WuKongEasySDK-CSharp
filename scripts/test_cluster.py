"""Reject false group-delivery receipts at the public SDK message boundary."""
import unittest
from interop_cluster import verify_group


class GroupTests(unittest.TestCase):
    def test_channel_identity_is_required(self):
        verify_group({'channelId': 'group', 'channelType': 2}, 'group')
        for message in ({'channelId': 'other', 'channelType': 2},
                        {'channelId': 'group', 'channelType': 1}, {},
                        {'channelId': 'group', 'channelType': True}):
            with self.subTest(message=message), self.assertRaises(AssertionError):
                verify_group(message, 'group')


class RecoveryPolicyTests(unittest.TestCase):
    def test_only_observed_system_rejection_can_retry_activation(self):
        from interop_cluster import retry_activation
        self.assertTrue(retry_activation({'ok': False, 'code': 15}, recovery=True))
        for result, recovery in [({'ok': True, 'code': 15}, True),
                                 ({'ok': False, 'code': 2}, True),
                                 ({'ok': False, 'code': None}, True),
                                 ({'ok': False, 'code': 15}, False)]:
            with self.subTest(result=result, recovery=recovery):
                self.assertFalse(retry_activation(result, recovery=recovery))
