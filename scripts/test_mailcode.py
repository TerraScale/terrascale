import unittest

from mailcode import code_from


class CodeFromTest(unittest.TestCase):
    def test_reads_the_code_from_a_terrascale_subject(self):
        self.assertEqual(code_from('482913 is your TerraScale verification code'), '482913')
        self.assertIsNone(code_from('Re: 482913 is your TerraScale verification code'))


if __name__ == '__main__':
    unittest.main()
