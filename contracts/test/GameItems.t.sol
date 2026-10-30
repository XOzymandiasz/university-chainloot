// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {GameItems} from "../src/GameItems.sol";

contract GameItemsTest is Test {
    GameItems private items;
    address private alice = makeAddr("alice");
    address private bob = makeAddr("bob");
    uint256 private constant ITEM = 1;

    function setUp() public {
        items = new GameItems();
        items.issue(alice, uint128(ITEM));
    }

    function testOwnerCanRedeemAndRemainsOwner() public {
        vm.prank(alice);
        items.redeem(ITEM);
        assertEq(items.ownerOf(ITEM), alice);
        assertEq(
            uint256(items.getStatus(ITEM)),
            uint256(GameItems.Status.REDEEMED)
        );
    }

    function testNonOwnerCannotRedeem() public {
        vm.prank(bob);
        vm.expectRevert(
            abi.encodeWithSelector(GameItems.NotItemOwner.selector, ITEM, bob)
        );
        items.redeem(ITEM);
        assertEq(
            uint256(items.getStatus(ITEM)),
            uint256(GameItems.Status.ACTIVE)
        );
    }

    function testCannotRedeemTwiceOrTransferAfterRedeem() public {
        vm.startPrank(alice);
        items.redeem(ITEM);
        vm.expectRevert(
            abi.encodeWithSelector(GameItems.ItemAlreadyRedeemed.selector, ITEM)
        );
        items.redeem(ITEM);
        vm.expectRevert(
            abi.encodeWithSelector(GameItems.ItemAlreadyRedeemed.selector, ITEM)
        );
        items.transferFrom(alice, bob, ITEM);
        vm.expectRevert(
            abi.encodeWithSelector(GameItems.ItemAlreadyRedeemed.selector, ITEM)
        );
        items.safeTransferFrom(alice, bob, ITEM);
        vm.stopPrank();
        assertEq(items.ownerOf(ITEM), alice);
        assertEq(
            uint256(items.getStatus(ITEM)),
            uint256(GameItems.Status.REDEEMED)
        );
    }

    function testNewOwnerCanRedeemAfterTransfer() public {
        vm.prank(alice);
        items.transferFrom(alice, bob, ITEM);
        vm.prank(bob);
        items.redeem(ITEM);
        assertEq(items.ownerOf(ITEM), bob);
        assertEq(
            uint256(items.getStatus(ITEM)),
            uint256(GameItems.Status.REDEEMED)
        );
    }

    function testCannotRedeemNonexistentItem() public {
        vm.expectRevert();
        items.redeem(999);
    }
}
