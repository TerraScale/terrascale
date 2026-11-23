"""Reads the newest TerraScale verification code mailed to an address.

recover-canaries.py uses it for the one-time owner password reset after a production reset.
The shared mailbox password is read from ~/.config/terrascale/mailbox-password (0600) and never
printed or passed as an argument.
"""
import email
import email.header
import email.utils
import imaplib
import os
import re
import ssl
import time

MAILBOX = 'mariogk@terrascale.tech'
HOST = 'imap.purelymail.com'
PASSWORD_FILE = os.path.expanduser('~/.config/terrascale/mailbox-password')
SUBJECT = re.compile(r'^(\d{6}) is your TerraScale')


def code_from(subject):
    match = SUBJECT.match(subject)
    return match.group(1) if match else None


def latest_code(address, since, wait=180):
    """Returns [code] for the newest code mail to address sent at or after since, or [] after wait seconds."""
    with open(PASSWORD_FILE) as handle:
        password = handle.read().strip()
    box = imaplib.IMAP4_SSL(HOST, 993, ssl_context=ssl.create_default_context(), timeout=20)
    box.login(MAILBOX, password)
    del password
    try:
        box.select('INBOX', readonly=True)
        deadline = time.monotonic() + wait
        while time.monotonic() < deadline:
            box.noop()
            status, data = box.uid('search', None, 'TO', '"' + address + '"')
            uids = sorted((int(uid) for uid in data[0].split()), reverse=True)[:10] if status == 'OK' else []
            for uid in uids:
                _, raw = box.uid('fetch', str(uid), '(BODY.PEEK[HEADER.FIELDS (SUBJECT DATE)])')
                message = email.message_from_bytes(raw[0][1])
                subject = str(email.header.make_header(email.header.decode_header(message.get('Subject', ''))))
                code = code_from(subject)
                if code and email.utils.parsedate_to_datetime(message.get('Date')) >= since:
                    return [code]
            time.sleep(3)
        return []
    finally:
        box.logout()
