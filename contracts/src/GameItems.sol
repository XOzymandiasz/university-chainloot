// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ERC721} from "../lib/openzeppelin-contracts/contracts/token/ERC721/ERC721.sol";

contract GameItems is ERC721 {
    enum Status {
        ACTIVE,
        REDEEMED
    }

    mapping(uint256 => Status) private statuses;

    error ItemAlreadyExists(uint256 tokenId);
    error NotItemOwner(uint256 tokenId, address caller);
    error ItemAlreadyRedeemed(uint256 tokenId);

    event ItemRedeemed(uint256 indexed tokenId, address indexed owner);

    constructor() ERC721("ChainLoot Items", "LOOT") {}

    function issue(address recipient, uint128 uuid) external returns (uint256) {
        uint256 tokenId = uint256(uuid);

        if (_ownerOf(tokenId) != address(0)) {
            revert ItemAlreadyExists(tokenId);
        }

        statuses[tokenId] = Status.ACTIVE;
        _safeMint(recipient, tokenId);

        return tokenId;
    }

    function transferFrom(address from, address to, uint256 tokenId) public override {
        if (ownerOf(tokenId) != msg.sender) {
            revert NotItemOwner(tokenId, msg.sender);
        }
        super.transferFrom(from, to, tokenId);
    }

    function redeem(uint256 tokenId) external {
        address owner = _ownerOf(tokenId);

        if (owner != msg.sender) {
            revert NotItemOwner(tokenId, msg.sender);
        }

        if (statuses[tokenId] == Status.REDEEMED) {
            revert ItemAlreadyRedeemed(tokenId);
        }

        statuses[tokenId] = Status.REDEEMED;
        emit ItemRedeemed(tokenId, owner);
    }

    function getStatus(uint256 tokenId) external view returns (Status) {
        ownerOf(tokenId);
        return statuses[tokenId];
    }

    function _update(
        address to,
        uint256 tokenId,
        address auth
    ) internal override returns (address) {
        if (
            _ownerOf(tokenId) != address(0) &&
            statuses[tokenId] == Status.REDEEMED
        ) {
            revert ItemAlreadyRedeemed(tokenId);
        }

        return super._update(to, tokenId, auth);
    }
}
