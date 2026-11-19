package main

import (
	"errors"
	"os"
	"path/filepath"
	"unsafe"

	"golang.org/x/sys/windows"
)

func init() {
	protectCredentialPath = protectWindowsCredentialPath
	verifyCredentialFile = verifyWindowsCredentialFile
}

func protectWindowsCredentialPath(path string, directory bool) error {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return err
	}
	current, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.OWNER_SECURITY_INFORMATION)
	if err != nil {
		return err
	}
	owner, _, err := current.Owner()
	if err != nil || owner == nil || !owner.Equals(user.User.Sid) {
		return errors.New("Credential path must belong to the current Windows account")
	}
	inherit := ""
	if directory {
		inherit = "OICI"
	}
	descriptor, err := windows.SecurityDescriptorFromString("D:P(A;" + inherit + ";FA;;;" + user.User.Sid.String() + ")")
	if err != nil {
		return err
	}
	acl, _, err := descriptor.DACL()
	if err != nil {
		return err
	}
	return windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, acl, nil)
}

func verifyWindowsCredentialFile(path string) error {
	info, err := os.Lstat(path)
	if err != nil || !info.Mode().IsRegular() {
		return errors.New("Agent credentials must be a regular file")
	}
	if err := verifyWindowsCredentialACL(filepath.Dir(path), true); err != nil {
		return err
	}
	return verifyWindowsCredentialACL(path, false)
}

func verifyWindowsCredentialACL(path string, directory bool) error {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return err
	}
	descriptor, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.OWNER_SECURITY_INFORMATION|windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		return err
	}
	owner, _, err := descriptor.Owner()
	if err != nil || owner == nil || !owner.Equals(user.User.Sid) {
		return errors.New("Credentials must belong to the current Windows account")
	}
	control, _, err := descriptor.Control()
	if err != nil || control&windows.SE_DACL_PROTECTED == 0 {
		return errors.New("Credentials must have a protected Windows ACL")
	}
	acl, _, err := descriptor.DACL()
	if err != nil || acl == nil || acl.AceCount != 1 {
		return errors.New("Credentials must allow only the current Windows account")
	}
	var ace *windows.ACCESS_ALLOWED_ACE
	if err := windows.GetAce(acl, 0, &ace); err != nil {
		return err
	}
	var flags byte
	if directory {
		flags = windows.OBJECT_INHERIT_ACE | windows.CONTAINER_INHERIT_ACE
	}
	if ace.Header.AceType != windows.ACCESS_ALLOWED_ACE_TYPE || ace.Header.AceFlags != flags ||
		ace.Mask != windows.ACCESS_MASK(windows.STANDARD_RIGHTS_REQUIRED|windows.SYNCHRONIZE|0x1ff) || !(*windows.SID)(unsafe.Pointer(&ace.SidStart)).Equals(user.User.Sid) {
		return errors.New("Credentials must grant full access only to the current Windows account")
	}
	return nil
}
