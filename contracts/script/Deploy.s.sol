// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Script, console2} from "forge-std/Script.sol";
import {GameItems} from "../src/GameItems.sol";

contract Deploy is Script {
    function run() external returns (GameItems items) {
        vm.startBroadcast(vm.envUint("PRIVATE_KEY"));
        items = new GameItems();
        vm.stopBroadcast();
        console2.log("GameItems deployed at:", address(items));
    }
}
