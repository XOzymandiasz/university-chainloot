// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {GameItems} from "../src/GameItems.sol";
import {IERC721Receiver} from "../lib/openzeppelin-contracts/contracts/token/ERC721/IERC721Receiver.sol";

contract GameItemsTest is Test {
    GameItems private items;
    address private alice = makeAddr("alice");
    address private bob = makeAddr("bob");
    address private charlie = makeAddr("charlie");
    uint256 private constant ITEM = 1;

    function setUp() public {
        items = new GameItems();
        items.issue(alice, uint128(ITEM));
    }

    function testIssueCreatesActiveItemAndRejectsDuplicate() public {
        assertEq(items.ownerOf(ITEM), alice);
        assertEq(uint256(items.getStatus(ITEM)), uint256(GameItems.Status.ACTIVE));
        vm.expectRevert(abi.encodeWithSelector(GameItems.ItemAlreadyExists.selector, ITEM));
        items.issue(bob, uint128(ITEM));
        assertEq(items.ownerOf(ITEM), alice);
    }

    function testOwnerCanTransferAndNonOwnerCannot() public {
        vm.prank(alice);
        items.transferFrom(alice, bob, ITEM);
        assertEq(items.ownerOf(ITEM), bob);
        assertEq(uint256(items.getStatus(ITEM)), uint256(GameItems.Status.ACTIVE));

        vm.prank(charlie);
        vm.expectRevert(abi.encodeWithSelector(GameItems.NotItemOwner.selector, ITEM, charlie));
        items.transferFrom(bob, charlie, ITEM);
        assertEq(items.ownerOf(ITEM), bob);
        assertEq(uint256(items.getStatus(ITEM)), uint256(GameItems.Status.ACTIVE));
    }

    function testApprovedOperatorCannotTransfer() public {
        vm.prank(alice);
        items.approve(charlie, ITEM);
        vm.prank(charlie);
        vm.expectRevert(abi.encodeWithSelector(GameItems.NotItemOwner.selector, ITEM, charlie));
        items.transferFrom(alice, bob, ITEM);
        assertEq(items.ownerOf(ITEM), alice);
    }

    function testApprovedOperatorCannotSafeTransfer() public {
        vm.prank(alice);
        items.setApprovalForAll(charlie, true);
        vm.prank(charlie);
        vm.expectRevert(abi.encodeWithSelector(GameItems.NotItemOwner.selector, ITEM, charlie));
        items.safeTransferFrom(alice, bob, ITEM);
        assertEq(items.ownerOf(ITEM), alice);
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

    function testItemRedeemedDuringMintStaysRedeemed() public {
        RedeemingReceiver receiver = new RedeemingReceiver(items);
        items.issue(address(receiver), 2);

        assertEq(items.ownerOf(2), address(receiver));
        assertEq(uint256(items.getStatus(2)), uint256(GameItems.Status.REDEEMED));
        vm.prank(address(receiver));
        vm.expectRevert(abi.encodeWithSelector(GameItems.ItemAlreadyRedeemed.selector, 2));
        items.transferFrom(address(receiver), bob, 2);
    }

    function testCannotRedeemNonexistentItem() public {
        vm.expectRevert();
        items.redeem(999);
    }
}

contract RedeemingReceiver is IERC721Receiver {
    GameItems private immutable items;

    constructor(GameItems _items) {
        items = _items;
    }

    function onERC721Received(address, address, uint256 tokenId, bytes calldata) external returns (bytes4) {
        items.redeem(tokenId);
        return IERC721Receiver.onERC721Received.selector;
    }
}
