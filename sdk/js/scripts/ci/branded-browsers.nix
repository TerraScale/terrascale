let
  pkgs = import (import ./nixpkgs-pin.nix) {
    system = "x86_64-linux";
    config.allowUnfree = true;
  };
in
[ pkgs.google-chrome pkgs.microsoft-edge ]
